using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed record GameEndpoint(string ProcessName, int Pid, string Protocol, string RemoteIp, int RemotePort, string State, bool LikelyGame, int Confidence, string ExecutablePath);

public static class GameScanner
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost","system","system idle process","lsass","services","wininit","spoolsv","explorer","dwm",
        "searchhost","searchindexer","runtimebroker","textinputhost","chrome","msedge","firefox","opera","brave",
        "discord","steamwebhelper","onedrive","dropbox","teams","outlook","powershell","cmd","conhost"
    };

    static readonly string[] Keywords =
    {
        "game","client","crossfire","valorant","fortnite","elden","apex","overwatch","warzone","cs2","csgo",
        "pubg","dota","league","minecraft","roblox","gta","battle","blizzard","riot","epic","steam","ubisoft"
    };

    public static async Task<List<GameEndpoint>> DiscoverAsync()
    {
        var foreground = GetForegroundProcessName();
        var foregroundPid = GetForegroundPid();
        var text = await RunAsync("netstat.exe", "-ano", 30000);
        var result = new List<GameEndpoint>();
        var processes = new Dictionary<int, (string Name, string Path)>();

        foreach (var line in text.Replace('\r', '\n').Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(?<proto>TCP|UDP)\s+(?<local>\S+)\s+(?<remote>\S+)(?:\s+(?<state>\S+))?\s+(?<pid>\d+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups["pid"].Value, out var pid)) continue;
            var proto = m.Groups["proto"].Value.ToUpperInvariant();
            var state = m.Groups["state"].Value;
            if (proto == "TCP" && !state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;

            var remote = m.Groups["remote"].Value.Trim();
            var colon = remote.LastIndexOf(':');
            var ip = colon > 0 ? remote[..colon].Trim('[', ']') : remote.Trim('[', ']');
            var port = colon > 0 && int.TryParse(remote[(colon + 1)..], out var parsedPort) ? parsedPort : 0;
            if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork || !IsPublic(ip)) continue;

            if (!processes.TryGetValue(pid, out var info))
            {
                try
                {
                    var p = Process.GetProcessById(pid); string path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch { }
                    info = (p.ProcessName, path); processes[pid] = info;
                }
                catch { continue; }
            }

            var confidence = Confidence(info.Name, pid, port, foreground, foregroundPid, info.Path);
            result.Add(new GameEndpoint(info.Name, pid, proto, ip, port, string.IsNullOrWhiteSpace(state) ? "ACTIVE" : state, confidence >= 55, confidence, info.Path));
        }

        return result
            .GroupBy(x => $"{x.Pid}|{x.Protocol}|{x.RemoteIp}|{x.RemotePort}")
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(x => x.Confidence)
            .ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.RemoteIp, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string GetForegroundProcessName()
    {
        try { var pid = GetForegroundPid(); return pid > 0 ? Process.GetProcessById(pid).ProcessName : ""; } catch { return ""; }
    }

    public static int GetForegroundPid()
    {
        try { var hwnd = GetForegroundWindow(); GetWindowThreadProcessId(hwnd, out var pid); return (int)pid; } catch { return 0; }
    }

    static int Confidence(string name, int pid, int port, string foreground, int foregroundPid, string path)
    {
        var n = name.ToLowerInvariant();
        if (Ignore.Contains(name)) return 0;
        var score = 0;
        if (pid == foregroundPid && foreground.Equals(name, StringComparison.OrdinalIgnoreCase)) score += 60;
        if (Keywords.Any(k => n.Contains(k))) score += 35;
        if (!string.IsNullOrWhiteSpace(path) && (path.Contains("\\Games\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\Steam\\steamapps\\common\\", StringComparison.OrdinalIgnoreCase))) score += 20;
        if (port >= 1024 && port != 443 && port != 80) score += 12;
        if (port is >= 10000 and <= 65535) score += 8;
        if (n.Contains("update") || n.Contains("updater")) score -= 25;
        return Math.Clamp(score, 0, 100);
    }

    static bool IsPublic(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)) return false;
        if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
        return true;
    }

    static async Task<string> RunAsync(string file, string args, int timeout)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 }) ?? throw new InvalidOperationException("Could not start " + file);
        var outputTask = p.StandardOutput.ReadToEndAsync(); var errorTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); } catch { try { p.Kill(true); } catch { } }
        return (await outputTask) + "\n" + (await errorTask);
    }
}
