using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed record GameEndpoint(string ProcessName, int Pid, string Protocol, string RemoteIp, int RemotePort, string State, bool LikelyGame);

public static class GameScanner
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    static readonly string[] Ignore = { "svchost", "system", "system idle process", "lsass", "services", "wininit", "spoolsv", "explorer", "dwm", "searchhost", "searchindexer", "runtimebroker", "textinputhost", "chrome", "msedge", "firefox", "opera", "brave", "discord", "steamwebhelper", "onedrive", "dropbox", "teams", "outlook", "powershell", "cmd", "conhost" };

    public static async Task<List<GameEndpoint>> DiscoverAsync()
    {
        var foreground = GetForegroundProcessName();
        var text = await RunAsync("netstat.exe", "-ano", 30000);
        var result = new List<GameEndpoint>();
        foreach (var line in text.Replace('\r', '\n').Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(?<proto>TCP|UDP)\s+(?<local>\S+)\s+(?<remote>\S+)(?:\s+(?<state>\S+))?\s+(?<pid>\d+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups["pid"].Value, out var pid)) continue;
            var remote = m.Groups["remote"].Value;
            var ip = remote;
            var port = 0;
            var lastColon = remote.LastIndexOf(':');
            if (lastColon > 0 && int.TryParse(remote[(lastColon + 1)..], out var p)) { ip = remote[..lastColon]; port = p; }
            ip = ip.Trim('[', ']');
            if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork || !IsPublic(ip)) continue;
            string processName;
            try { processName = Process.GetProcessById(pid).ProcessName; } catch { continue; }
            var state = m.Groups["state"].Value;
            if (m.Groups["proto"].Value.Equals("TCP", StringComparison.OrdinalIgnoreCase) && !state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;
            var likely = IsLikelyGame(processName, port, foreground);
            result.Add(new GameEndpoint(processName, pid, m.Groups["proto"].Value.ToUpperInvariant(), ip, port, string.IsNullOrWhiteSpace(state) ? "ACTIVE" : state, likely));
        }
        return result.GroupBy(x => $"{x.Pid}|{x.Protocol}|{x.RemoteIp}|{x.RemotePort}").Select(g => g.First()).OrderByDescending(x => x.LikelyGame).ThenBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.RemoteIp, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string GetForegroundProcessName()
    {
        try { var hwnd = GetForegroundWindow(); GetWindowThreadProcessId(hwnd, out var pid); if (pid > 0) return Process.GetProcessById((int)pid).ProcessName; } catch { }
        return "";
    }

    static bool IsLikelyGame(string name, int port, string foreground)
    {
        if (!string.IsNullOrWhiteSpace(foreground) && name.Equals(foreground, StringComparison.OrdinalIgnoreCase)) return true;
        var n = name.ToLowerInvariant();
        if (Ignore.Contains(n)) return false;
        var keywords = new[] { "game", "client", "launcher", "crossfire", "valorant", "fortnite", "elden", "apex", "overwatch", "warzone", "cs2", "csgo", "pubg", "dota", "league", "minecraft", "roblox", "gta", "battle", "blizzard", "riot", "epic" };
        if (keywords.Any(k => n.Contains(k))) return true;
        return port >= 1000 && !n.Contains("update");
    }

    static bool IsPublic(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        return true;
    }

    static async Task<string> RunAsync(string file, string args, int timeout)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 }) ?? throw new InvalidOperationException("Could not start " + file);
        var output = await p.StandardOutput.ReadToEndAsync();
        var error = await p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(cts.Token); } catch { try { p.Kill(true); } catch { } }
        return output + (string.IsNullOrWhiteSpace(error) ? "" : "\n" + error);
    }
}
