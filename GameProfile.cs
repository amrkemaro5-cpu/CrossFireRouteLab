using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace CrossFireRouteLab;

public sealed class GameProfile
{
    public string Key { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string IconPath { get; set; } = "";
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string LastBestEndpoint { get; set; } = "";
    public double LastScore { get; set; }
    public int Observations { get; set; }
    public List<string> RecentPaths { get; set; } = new();
    public List<string> RecentEndpoints { get; set; } = new();
}

public static class GameProfileStore
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab");
    static readonly string ProfilesFile = Path.Combine(Root, "profiles.json");
    static readonly object Sync = new();

    static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chatgpt", "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
        "discord", "slack", "teams", "outlook", "onedrive", "dropbox", "whatsapp", "whatsapp.root", "nte",
        "steam", "steamwebhelper", "epicgameslauncher", "epicwebhelper",
        "updater", "update", "searchhost", "searchindexer", "runtimebroker",
        "explorer", "dwm", "svchost", "lsass", "services", "wininit",
        "spoolsv", "powershell", "pwsh", "cmd", "conhost", "taskmgr"
    };

    public static bool IsBlocked(string processName)
    {
        var name = Path.GetFileNameWithoutExtension(processName ?? "");
        return string.IsNullOrWhiteSpace(name) || BlockedProcesses.Contains(name) ||
               name.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("whatsapp", StringComparison.OrdinalIgnoreCase);
    }

    public static List<GameProfile> Load()
    {
        try
        {
            if (!File.Exists(ProfilesFile)) return new();
            var profiles = JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllText(ProfilesFile)) ?? new();
            var clean = profiles.Where(p => !IsBlocked(p.ProcessName) && !IsBlocked(Path.GetFileNameWithoutExtension(p.ExecutablePath))).ToList();
            if (clean.Count != profiles.Count) Save(clean);
            return clean;
        }
        catch { return new(); }
    }

    public static GameProfile Touch(string processName, string exePath)
    {
        if (IsBlocked(processName)) throw new InvalidOperationException("Blocked non-game process.");
        lock (Sync)
        {
            Directory.CreateDirectory(Root);
            var profiles = Load();
            var key = MakeKey(processName, exePath);
            var p = profiles.FirstOrDefault(x => x.Key == key);
            if (p == null)
            {
                p = new GameProfile { Key = key, ProcessName = processName, ExecutablePath = exePath, DisplayName = PrettyName(processName, exePath), FirstSeenUtc = DateTime.UtcNow };
                profiles.Add(p);
            }
            p.ProcessName = processName;
            p.ExecutablePath = exePath;
            p.DisplayName = PrettyName(processName, exePath);
            p.LastSeenUtc = DateTime.UtcNow;
            p.IconPath = EnsureIcon(p);
            Save(profiles);
            return p;
        }
    }

    public static void Record(GameProfile profile, string endpoint, double score, string pathSignature)
    {
        if (IsBlocked(profile.ProcessName)) return;
        lock (Sync)
        {
            var profiles = Load();
            var p = profiles.FirstOrDefault(x => x.Key == profile.Key) ?? profile;
            p.LastSeenUtc = DateTime.UtcNow;
            p.LastBestEndpoint = endpoint;
            p.LastScore = score;
            p.Observations++;
            if (!string.IsNullOrWhiteSpace(endpoint)) AddLimited(p.RecentEndpoints, endpoint, 12);
            if (!string.IsNullOrWhiteSpace(pathSignature)) AddLimited(p.RecentPaths, pathSignature, 12);
            p.IconPath = EnsureIcon(p);
            Save(profiles);
        }
    }

    static void AddLimited(List<string> list, string value, int max)
    {
        list.Remove(value);
        list.Insert(0, value);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }

    static string MakeKey(string processName, string exePath) => (processName + "|" + exePath).ToLowerInvariant();

    static string PrettyName(string processName, string exePath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                var v = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(v.ProductName) && !v.ProductName.Equals("Microsoft Windows", StringComparison.OrdinalIgnoreCase)) return v.ProductName.Trim();
                if (!string.IsNullOrWhiteSpace(v.FileDescription) && !v.FileDescription.Equals(processName, StringComparison.OrdinalIgnoreCase)) return v.FileDescription.Trim();
            }
        }
        catch { }
        var s = Path.GetFileNameWithoutExtension(processName) ?? processName;
        return string.IsNullOrWhiteSpace(s) ? processName : char.ToUpperInvariant(s[0]) + s[1..];
    }

    static string EnsureIcon(GameProfile p)
    {
        try
        {
            var dir = Path.Combine(Root, "icons");
            Directory.CreateDirectory(dir);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.Key))).Substring(0, 16);
            var path = Path.Combine(dir, hash + ".png");
            if (File.Exists(path)) return path;
            using var icon = !string.IsNullOrWhiteSpace(p.ExecutablePath) && File.Exists(p.ExecutablePath) ? Icon.ExtractAssociatedIcon(p.ExecutablePath) : null;
            using var bmp = new Bitmap(96, 96, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.FromArgb(5, 10, 20));
            if (icon != null)
            {
                using var ib = icon.ToBitmap();
                g.DrawImage(ib, new Rectangle(8, 8, 80, 80));
            }
            else
            {
                using var pen = new Pen(Color.FromArgb(0, 224, 255), 5);
                using var brush = new SolidBrush(Color.FromArgb(20, 40, 66));
                g.FillRoundedRectangle(brush, new RectangleF(12, 22, 72, 52), 14);
                g.DrawRoundedRectangle(pen, new Rectangle(12, 22, 72, 52), 14);
                g.DrawLine(pen, 26, 48, 40, 48);
                g.DrawLine(pen, 33, 41, 33, 55);
                g.FillEllipse(Brushes.White, 58, 44, 6, 6);
                g.FillEllipse(Brushes.White, 70, 44, 6, 6);
            }
            bmp.Save(path, ImageFormat.Png);
            return path;
        }
        catch { return ""; }
    }

    static void Save(List<GameProfile> profiles)
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
    }
}

static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF r, float radius)
    {
        using var path = Rounded(r, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle r, int radius)
    {
        using var path = Rounded(r, radius);
        g.DrawPath(pen, path);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
