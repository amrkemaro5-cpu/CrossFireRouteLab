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
        "applicationframehost", "sihost", "ctfmon", "fontdrvhost", "audiodg",
        "gameroutelab"
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

    static readonly string[] FutureGameWords =
    {
        "game", "gameclient", "client64", "x64client", "win64", "arena", "battle"
    };

    public static async Task<List<GameEndpoint>> DiscoverAsync()
    {
        var foreground = GetForegroundPid();
        var text = await RunAsync("netstat.exe", "-ano", 30000);
        var cache = new Dictionary<int, (string Name, string Path, string Title)>();
        var result = new List<GameEndpoint>();

        // First collect real public sockets. This remains the preferred source because
        // it gives us the actual remote endpoint used by the game.
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

            if (!TryGetProcessInfo(pid, out var info)) continue;
            cache[pid] = info;

            var score = Confidence(info.Name, info.Path, info.Title, pid, foreground, protocol, port, state, true);
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

        // Critical fallback: a game must not disappear simply because its anti-cheat,
        // renderer, UDP transport, firewall state, or current game screen prevents
        // netstat from exposing a public socket at the exact scan moment. Enumerate
        // running processes separately and identify the game by executable/path/window.
        // This is what makes REFRESH GAMES and AUTO ANALYZE useful before an endpoint
        // is visible. A synthetic endpoint is never used for ping/trace because its
        // RemotePort is 0; it only represents a detected game process.
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!TryGetProcessInfo(process.Id, out var info)) continue;
                if (GameProfileStore.IsBlocked(info.Name)) continue;
                if (cache.ContainsKey(process.Id)) continue;

                var score = Confidence(info.Name, info.Path, info.Title, process.Id, foreground, "", 0, "NO_PUBLIC_SOCKET", false);
                if (!IsStrongProcessCandidate(info.Name, info.Path, info.Title, process.Id, foreground, score)) continue;

                result.Add(new GameEndpoint(
                    info.Name,
                    process.Id,
                    "",
                    "",
                    0,
                    "NO_PUBLIC_SOCKET",
                    true,
                    score,
                    info.Path));
            }
            catch { }
            finally { process.Dispose(); }
        }

        // If the same PID has real sockets, keep those records as the useful endpoint
        // evidence and retain the process-only record only when it is the best proof
        // of game identity. This also prevents duplicate memory entries.
        return result
            .GroupBy(x => x.Pid)
            .SelectMany(group =>
            {
                var real = group.Where(x => x.RemotePort > 0).ToList();
                if (real.Count > 0) return real;
                return group.Take(1);
            })
            .GroupBy(x => $"{x.Pid}|{x.Protocol}|{x.RemoteIp}|{x.RemotePort}")
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(x => x.Confidence)
            .ToList();
    }

    static bool TryGetProcessInfo(int pid, out (string Name, string Path, string Title) info)
    {
        info = default;
        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;
            var path = "";
            try { path = process.MainModule?.FileName ?? ""; } catch { }
            var title = "";
            try { title = process.MainWindowTitle ?? ""; } catch { }
            info = (name, path, title);
            return true;
        }
        catch { return false; }
    }

    static bool IsStrongProcessCandidate(string name, string path, string title, int pid, int foreground, int score)
    {
        var process = Path.GetFileNameWithoutExtension(name ?? "");
        var lowerProcess = process.ToLowerInvariant();
        var lowerPath = (path ?? "").ToLowerInvariant();
        var lowerTitle = (title ?? "").ToLowerInvariant();
        var known = GameWords.Any(w => lowerProcess.Contains(w, StringComparison.OrdinalIgnoreCase));
        var folder = GameFolders.Any(w => lowerPath.Contains(w, StringComparison.OrdinalIgnoreCase));
        var futureName = FutureGameWords.Any(w => lowerProcess.Contains(w, StringComparison.OrdinalIgnoreCase));
        var futureTitle = FutureGameWords.Any(w => lowerTitle.Contains(w, StringComparison.OrdinalIgnoreCase));
        var foreground = pid == foreground;
        var hasTitle = !string.IsNullOrWhiteSpace(title);

        if (known && score >= 45) return true;
        if (folder && score >= 45) return true;
        if (foreground && hasTitle && (futureName || futureTitle) && score >= 45) return true;
        return false;
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

    static int Confidence(string name, string path, string title, int pid, int foreground, string protocol, int port, string state, bool hasPublicSocket)
    {
        if (Ignore.Contains(name) || GameProfileStore.IsBlocked(name)) return 0;

        var process = name.ToLowerInvariant();
        var lowerPath = path.ToLowerInvariant();
        var lowerTitle = title.ToLowerInvariant();
        var all = process + " " + lowerTitle + " " + lowerPath;

        if (all.Contains("chatgpt") || all.Contains("microsoftedge") || all.Contains("chrome.exe") ||
            all.Contains("firefox.exe") || all.Contains("discord.exe") || all.Contains("gameroutelab")) return 0;

        var score = 0;
        var knownGameName = GameWords.Any(w => process.Contains(w, StringComparison.OrdinalIgnoreCase));
        var gameFolder = GameFolders.Any(f => lowerPath.Contains(f, StringComparison.OrdinalIgnoreCase));
        var gameTitle = GameWords.Any(w => lowerTitle.Contains(w, StringComparison.OrdinalIgnoreCase));
        var foregroundProcess = pid == foreground;
        var interactiveSocket = hasPublicSocket && (state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) ||
                                                     state.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase));
        var hasWindowTitle = !string.IsNullOrWhiteSpace(title);
        var highPort = port >= 1024 && port <= 65535;
        var webPort = port is 80 or 443;
        var launcher = LauncherWords.Any(w => process.Contains(w, StringComparison.OrdinalIgnoreCase));
        var futureName = FutureGameWords.Any(w => process.Contains(w, StringComparison.OrdinalIgnoreCase));
        var futureTitle = FutureGameWords.Any(w => lowerTitle.Contains(w, StringComparison.OrdinalIgnoreCase));

        if (knownGameName) score += 55;
        if (gameFolder) score += 30;
        if (gameTitle) score += 20;
        if (foregroundProcess) score += 24;
        if (hasWindowTitle) score += 8;
        if (interactiveSocket) score += 12;
        if (highPort) score += 8;
        if (webPort) score -= 5;
        if (launcher) score -= 28;
        if (futureName) score += 20;
        if (futureTitle) score += 18;

        // Unknown foreground game clients are supported when their executable/title
        // looks game-like. We still require a real window and a game-oriented token;
        // an arbitrary foreground application is never promoted to game memory.
        if (!hasPublicSocket && foregroundProcess && hasWindowTitle && (futureName || futureTitle))
            score += 10;

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
