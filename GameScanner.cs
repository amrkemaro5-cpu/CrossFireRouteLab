using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

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
        "searchhost", "searchindexer", "runtimebroker", "textinputhost",
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
        "discord", "slack", "teams", "outlook", "onedrive", "dropbox",
        "powershell", "pwsh", "cmd", "conhost", "chatgpt",
        "steamwebhelper", "steam", "epicwebhelper", "epicgameslauncher",
        "battle.net", "blizzard update agent", "riotclientservices", "riotclientux",
        "updater", "update", "taskmgr", "searchapp", "widgets", "widgetservice",
        "applicationframehost", "sihost", "ctfmon", "fontdrvhost", "audiodg"
    };

    static readonly string[] GameWords =
    {
        "crossfire", "valorant", "fortnite", "apex", "overwatch", "warzone", "callofduty",
        "pubg", "dota", "leagueoflegends", "minecraft", "roblox", "eldenring", "battlefield",
        "rainbowsix", "destiny", "gameclient", "game-client", "gameserver", "gameclient64"
    };

    static readonly string[] GameFolders =
    {
        "\\games\\", "\\steamapps\\common\\", "\\epic games\\", "\\riot games\\",
        "\\valorant\\", "\\crossfire\\", "\\garena\\", "\\z8games\\",
        "\\blizzard\\", "\\ubisoft\\", "\\battle.net\\", "\\playstation\\",
        "\\xboxgames\\", "\\windowsapps\\"
    };

    static readonly string[] LauncherWords =
    {
        "launcher", "bootstrapper", "patcher", "updater", "webhelper", "crashhandler"
    };

    public static async Task<List<GameEndpoint>> DiscoverAsync()
    {
        var foreground = GetForegroundPid();
        var text = await RunAsync("netstat.exe", "-ano", 30000);
        var cache = new Dictionary<int, (string Name, string Path, string Title)>();
        var result = new List<GameEndpoint>();

        foreach (var line in text.Replace('\r', '\n').Split('\n'))
        {
            var m = Regex.Match(
                line,
                @"^\s*(TCP|UDP)\s+(\S+)\s+(\S+)(?:\s+(\S+))?\s+(\d+)\s*$",
                RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[5].Value, out var pid) || pid <= 0) continue;

            var protocol = m.Groups[1].Value.ToUpperInvariant();
            var state = string.IsNullOrWhiteSpace(m.Groups[4].Value) ? "ACTIVE" : m.Groups[4].Value;
            if (protocol == "TCP" && !state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;

            var remote = m.Groups[3].Value;
            var colon = remote.LastIndexOf(':');
            var ip = colon > 0 ? remote[..colon].Trim('[', ']') : remote.Trim('[', ']');
            var port = colon > 0 && int.TryParse(remote[(colon + 1)..], out var parsedPort) ? parsedPort : 0;
            if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork || !IsPublic(ip)) continue;
            if (port <= 0) continue;

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

            var score = Confidence(info.Name, info.Path, info.Title, pid, foreground, protocol, port, state);
            if (score < 30 || GameProfileStore.IsBlocked(info.Name)) continue;

            result.Add(new GameEndpoint(
                info.Name,
                pid,
                protocol,
                ip,
                port,
                state,
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

    static int Confidence(string name, string path, string title, int pid, int foreground, string protocol, int port, string state)
    {
        if (Ignore.Contains(name) || GameProfileStore.IsBlocked(name)) return 0;

        var process = name.ToLowerInvariant();
        var lowerPath = path.ToLowerInvariant();
        var lowerTitle = title.ToLowerInvariant();
        var all = process + " " + lowerTitle + " " + lowerPath;

        // Explicitly prevent the app itself, browsers and browser-like shells from
        // entering game memory even when they have many public connections.
        if (all.Contains("chatgpt") || all.Contains("microsoftedge") || all.Contains("chrome.exe") ||
            all.Contains("firefox.exe") || all.Contains("discord.exe")) return 0;

        var score = 0;
        var knownGameName = GameWords.Any(w => process.Contains(w, StringComparison.OrdinalIgnoreCase));
        var gameFolder = GameFolders.Any(f => lowerPath.Contains(f, StringComparison.OrdinalIgnoreCase));
        var gameTitle = GameWords.Any(w => lowerTitle.Contains(w, StringComparison.OrdinalIgnoreCase));
        var foregroundProcess = pid == foreground;
        var interactiveSocket = state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) ||
                                state.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
        var hasWindowTitle = !string.IsNullOrWhiteSpace(title);
        var highPort = port >= 1024 && port <= 65535;
        var webPort = port is 80 or 443;
        var launcher = LauncherWords.Any(w => process.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (knownGameName) score += 55;
        if (gameFolder) score += 30;
        if (gameTitle) score += 20;
        if (foregroundProcess) score += 24;
        if (hasWindowTitle) score += 8;
        if (interactiveSocket) score += 12;
        if (highPort) score += 8;
        if (webPort) score -= 5;
        if (launcher) score -= 28;

        // Future-game fallback: an unknown foreground application with a real window
        // and an active public game-like socket is allowed, but only after all known
        // browser/system exclusions above. This avoids hard-coding every future game.
        if (foregroundProcess && hasWindowTitle && interactiveSocket && highPort)
            score += 18;

        // A background process needs stronger evidence than a foreground game.
        if (!foregroundProcess && !knownGameName && !gameFolder && !gameTitle)
            score -= 20;

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
