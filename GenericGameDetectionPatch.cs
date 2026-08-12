using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Generic game discovery bridge for v10. The legacy dashboard keeps a small
/// KnownGames list; this patch feeds dynamically discovered game process names
/// into that existing pipeline so route analysis is not CrossFire-only.
/// </summary>
public static class GenericGameDetectionPatch
{
    static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "svchost", "lsass", "services", "wininit", "spoolsv", "explorer", "dwm",
        "searchhost", "searchindexer", "runtimebroker", "textinputhost", "applicationframehost", "sihost",
        "ctfmon", "fontdrvhost", "audiodg", "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
        "discord", "slack", "teams", "outlook", "onedrive", "dropbox", "whatsapp", "whatsapp.root",
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper", "riotclientservices", "riotclientux",
        "battle.net", "blizzard update agent", "powershell", "pwsh", "cmd", "conhost", "taskmgr", "chatgpt",
        "gameroutelab"
    };

    static readonly string[] GameWords =
    {
        "game", "client", "gameclient", "client64", "win64", "shipping", "launcher64",
        "valorant", "cs2", "csgo", "counterstrike", "fortnite", "apex", "r5apex", "pubg",
        "warzone", "callofduty", "overwatch", "leagueoflegends", "dota2", "minecraft", "roblox",
        "eldenring", "battlefield", "rainbowsix", "destiny", "paladins", "rocketleague", "genshin",
        "honkai", "monsterhunter", "deadbydaylight", "rustclient", "arma", "dayz", "crossfire"
    };

    static readonly string[] GameFolders =
    {
        "\\steamapps\\common\\", "\\epic games\\", "\\riot games\\", "\\valorant\\", "\\crossfire\\",
        "\\garena\\", "\\z8games\\", "\\blizzard\\", "\\ubisoft\\", "\\battle.net\\",
        "\\games\\", "\\game\\", "\\xboxgames\\"
    };

    static readonly string[] LauncherWords = { "launcher", "bootstrapper", "patcher", "updater", "webhelper", "crashhandler" };

    public static void Apply(GameRouteLabV10Form form)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 1800 };
        timer.Tick += async (_, _) =>
        {
            try { await DiscoverAndFeedAsync(form).ConfigureAwait(true); } catch { }
        };
        timer.Start();
        form.FormClosed += (_, _) => timer.Stop();
        _ = DiscoverAndFeedAsync(form);
    }

    static async Task DiscoverAndFeedAsync(GameRouteLabV10Form form)
    {
        var foregroundPid = GameScanner.GetForegroundPid();
        var discovered = await GameScanner.DiscoverAsync().ConfigureAwait(true);
        var candidates = new Dictionary<int, Candidate>();

        foreach (var endpoint in discovered)
        {
            if (endpoint.Pid <= 0 || IsBlocked(endpoint.ProcessName)) continue;
            var c = candidates.TryGetValue(endpoint.Pid, out var existing)
                ? existing
                : new Candidate(endpoint.Pid, endpoint.ProcessName, endpoint.ExecutablePath, "");
            c.PublicSocket = true;
            c.Score = Math.Max(c.Score, 55);
            candidates[endpoint.Pid] = c;
        }

        if (foregroundPid > 0 && TryGetProcessInfo(foregroundPid, out var fg) && !IsBlocked(fg.Name))
        {
            var c = candidates.TryGetValue(foregroundPid, out var existing)
                ? existing
                : new Candidate(foregroundPid, fg.Name, fg.Path, fg.Title);
            c.Name = fg.Name;
            c.Path = fg.Path;
            c.Title = fg.Title;
            c.Foreground = true;
            c.Score += 25;
            if (!string.IsNullOrWhiteSpace(fg.Title)) c.Score += 10;
            if (IsGamePath(fg.Path)) c.Score += 35;
            if (HasGameWord(fg.Name) || HasGameWord(fg.Title)) c.Score += 30;
            candidates[foregroundPid] = c;
        }

        foreach (var c in candidates.Values)
            if (IsLikelyGame(c)) FeedProcessName(form, c.Name);
    }

    static bool IsLikelyGame(Candidate c)
    {
        if (IsBlocked(c.Name)) return false;
        var path = c.Path ?? "";
        if (path.Contains("\\Windows\\", StringComparison.OrdinalIgnoreCase) || path.Contains("\\System32\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (LauncherWords.Any(x => c.Name.Contains(x, StringComparison.OrdinalIgnoreCase)) && !HasGameWord(c.Name)) return false;

        if (IsGamePath(path) && (c.PublicSocket || c.Foreground)) return true;
        if (HasGameWord(c.Name) && (c.PublicSocket || c.Foreground)) return true;
        if (c.Foreground && c.PublicSocket && !string.IsNullOrWhiteSpace(c.Title) && c.Score >= 90)
            return true;
        return false;
    }

    static void FeedProcessName(GameRouteLabV10Form form, string processName)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var field = typeof(GameRouteLabV10Form).GetField("customGames", flags);
        if (field?.GetValue(form) is not List<string> list) return;
        if (list.Any(x => x.Equals(processName, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(processName);

        // Reuse the existing game-memory renderer and selection pipeline.
        try { typeof(GameRouteLabV10Form).GetMethod("RefreshGames", flags)?.Invoke(form, new object[] { false }); } catch { }
    }

    static bool TryGetProcessInfo(int pid, out (string Name, string Path, string Title) info)
    {
        info = default;
        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName;
            var path = ""; try { path = p.MainModule?.FileName ?? ""; } catch { }
            var title = ""; try { title = p.MainWindowTitle ?? ""; } catch { }
            info = (name, path, title);
            return true;
        }
        catch { return false; }
    }

    static bool IsBlocked(string name)
    {
        var n = Path.GetFileNameWithoutExtension(name ?? "");
        return string.IsNullOrWhiteSpace(n) || Blocked.Contains(n) || n.Contains("chatgpt", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsGamePath(string path) => GameFolders.Any(x => path.Contains(x, StringComparison.OrdinalIgnoreCase));

    static bool HasGameWord(string value)
    {
        var s = Path.GetFileNameWithoutExtension(value ?? "");
        return GameWords.Any(x => s.Contains(x, StringComparison.OrdinalIgnoreCase));
    }

    sealed class Candidate
    {
        public int Pid { get; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Title { get; set; }
        public bool Foreground { get; set; }
        public bool PublicSocket { get; set; }
        public int Score { get; set; }
        public Candidate(int pid, string name, string path, string title) { Pid = pid; Name = name; Path = path; Title = title; }
    }
}
