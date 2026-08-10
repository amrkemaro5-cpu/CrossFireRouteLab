using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

public sealed record GameEndpoint(
    string ProcessName,
    int Pid,
    string Protocol,
    string RemoteIp,
    int RemotePort,
    string State,
    bool LikelyGame,
    int Confidence,
    string ExecutablePath);

public static class GameScanner
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "svchost", "lsass", "services", "wininit", "spoolsv", "explorer", "dwm",
        "searchhost", "searchindexer", "runtimebroker", "textinputhost", "chrome", "msedge", "firefox",
        "opera", "brave", "vivaldi", "discord", "slack", "teams", "outlook", "onedrive", "dropbox",
        "powershell", "pwsh", "cmd", "conhost", "chatgpt", "steamwebhelper", "steam", "epicwebhelper",
        "epicgameslauncher", "updater", "update", "taskmgr"
    };

    static readonly string[] GameWords =
    {
        "crossfire", "valorant", "fortnite", "apex", "overwatch", "warzone", "callofduty", "cod",
        "cs2", "csgo", "pubg", "dota", "leagueoflegends", "league", "minecraft", "roblox", "gta",
        "eldenring", "battlefield", "rainbowsix", "r6", "destiny", "gameclient", "game"
    };

    static readonly string[] GameFolders =
    {
        "\\games\\", "\\steamapps\\common\\", "\\epic games\\", "\\riot games\\", "\\valorant\\",
        "\\crossfire\\", "\\garena\\", "\\z8games\\", "\\blizzard\\", "\\ubisoft\\"
    };

    public static async Task<List<GameEndpoint>> DiscoverAsync()
    {
        var foreground = GetForegroundPid();
        var text = await RunAsync("netstat.exe", "-ano", 30000);
        var cache = new Dictionary<int, (string Name, string Path, string Title)>();
        var result = new List<GameEndpoint>();

        foreach (var line in text.Replace('\r', '\n').Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*(TCP|UDP)\s+(\S+)\s+(\S+)(?:\s+(\S+))?\s+(\d+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[5].Value, out var pid) || pid <= 0) continue;

            var protocol = m.Groups[1].Value.ToUpperInvariant();
            var state = m.Groups[4].Value;
            if (protocol == "TCP" && !state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;

            var remote = m.Groups[3].Value;
            var colon = remote.LastIndexOf(':');
            var ip = colon > 0 ? remote[..colon].Trim('[', ']') : remote.Trim('[', ']');
            var port = colon > 0 && int.TryParse(remote[(colon + 1)..], out var parsedPort) ? parsedPort : 0;
            if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork || !IsPublic(ip)) continue;

            if (!cache.TryGetValue(pid, out var info))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    var path = "";
                    try { path = process.MainModule?.FileName ?? ""; } catch { }
                    info = (process.ProcessName, path, process.MainWindowTitle);
                    cache[pid] = info;
                }
                catch { continue; }
            }

            var score = Confidence(info.Name, info.Path, info.Title, pid, foreground, port, state);
            if (score < 30 || GameProfileStore.IsBlocked(info.Name)) continue;

            result.Add(new GameEndpoint(
                info.Name,
                pid,
                protocol,
                ip,
                port,
                string.IsNullOrWhiteSpace(state) ? "ACTIVE" : state,
                score >= 45,
                score,
                info.Path));
        }

        return result
            .GroupBy(x => $"{x.Pid}|{x.Protocol}|{x.RemoteIp}|{x.RemotePort}")
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(x => x.Confidence)
            .ToList();
    }

    public static int GetForegroundPid()
    {
        try
        {
            var handle = GetForegroundWindow();
            GetWindowThreadProcessId(handle, out var pid);
            return (int)pid;
        }
        catch { return 0; }
    }

    public static string GetForegroundProcessName()
    {
        try
        {
            var pid = GetForegroundPid();
            return pid > 0 ? Process.GetProcessById(pid).ProcessName : "";
        }
        catch { return ""; }
    }

    static int Confidence(string name, string path, string title, int pid, int foreground, int port, string state)
    {
        if (Ignore.Contains(name) || GameProfileStore.IsBlocked(name)) return 0;

        var process = name.ToLowerInvariant();
        var all = (process + " " + title + " " + path).ToLowerInvariant();
        if (all.Contains("chatgpt") || all.Contains("microsoftedge") || all.Contains("chrome.exe")) return 0;

        var score = 0;
        var word = GameWords.Any(w => process.Contains(w, StringComparison.OrdinalIgnoreCase));
        var folder = GameFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));
        var titleGame = GameWords.Any(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));
        var foregroundProcess = pid == foreground;
        var likelyInteractiveSocket = state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) || state.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);

        if (word) score += 52;
        if (folder) score += 28;
        if (titleGame) score += 18;
        if (foregroundProcess) score += 30;
        if (likelyInteractiveSocket) score += 12;
        if (port >= 10000 && port <= 65535) score += 8;
        if (port is 80 or 443) score -= 4;
        if (process.Contains("launcher")) score -= 25;
        if (process.Contains("helper")) score -= 20;
        if (process.Contains("updater")) score -= 30;

        // Generic-game fallback: the foreground process with a live public socket can be
        // a game even when its name is unknown. Known non-game processes are filtered first.
        if (foregroundProcess && likelyInteractiveSocket) score += 18;

        return Math.Clamp(score, 0, 100);
    }

    static bool IsPublic(string ip)
    {
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
        return true;
    }

    static async Task<string> RunAsync(string file, string args, int timeout)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            });
            if (process == null) return "";

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var cancellation = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch { try { process.Kill(true); } catch { } }
            return await output + "\n" + await error;
        }
        catch { return ""; }
    }
}
