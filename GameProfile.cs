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

    public static List<GameProfile> Load()
    {
        try
        {
            if (!File.Exists(ProfilesFile)) return new();
            return JsonSerializer.Deserialize<List<GameProfile>>(File.ReadAllText(ProfilesFile)) ?? new();
        }
        catch { return new(); }
    }

    public static GameProfile Touch(string processName, string exePath)
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Root);
            var profiles = Load();
            var key = MakeKey(processName, exePath);
            var p = profiles.FirstOrDefault(x => x.Key == key);
            if (p == null)
            {
                p = new GameProfile { Key = key, ProcessName = processName, ExecutablePath = exePath, DisplayName = PrettyName(processName), FirstSeenUtc = DateTime.UtcNow };
                profiles.Add(p);
            }
            p.ProcessName = processName;
            p.ExecutablePath = exePath;
            p.DisplayName = PrettyName(processName);
            p.LastSeenUtc = DateTime.UtcNow;
            p.IconPath = EnsureIcon(p);
            Save(profiles);
            return p;
        }
    }

    public static void Record(GameProfile profile, string endpoint, double score, string pathSignature)
    {
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
            Save(profiles);
        }
    }

    static void AddLimited(List<string> list, string value, int max)
    {
        list.Remove(value); list.Insert(0, value);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }

    static string MakeKey(string processName, string exePath) => (processName + "|" + exePath).ToLowerInvariant();

    static string PrettyName(string processName)
    {
        var s = Path.GetFileNameWithoutExtension(processName) ?? processName;
        return string.IsNullOrWhiteSpace(s) ? processName : char.ToUpperInvariant(s[0]) + s[1..];
    }

    static string EnsureIcon(GameProfile p)
    {
        try
        {
            var iconDir = Path.Combine(Root, "icons"); Directory.CreateDirectory(iconDir);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.Key))).Substring(0, 16);
            var path = Path.Combine(iconDir, hash + ".png");
            if (File.Exists(path)) return path;

            using var icon = !string.IsNullOrWhiteSpace(p.ExecutablePath) && File.Exists(p.ExecutablePath) ? Icon.ExtractAssociatedIcon(p.ExecutablePath) : null;
            using var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp); g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.FromArgb(28, 36, 48));
            if (icon != null)
            {
                using var ib = icon.ToBitmap(); g.DrawImage(ib, new Rectangle(4, 4, 56, 56));
            }
            else
            {
                using var pen = new Pen(Color.FromArgb(80, 210, 220), 4); using var brush = new SolidBrush(Color.FromArgb(45, 60, 80));
                g.FillRoundedRectangle(brush, new RectangleF(7, 17, 50, 30), 10); g.DrawRoundedRectangle(pen, new Rectangle(7, 17, 50, 30), 10);
                g.DrawLine(pen, 18, 32, 28, 32); g.DrawLine(pen, 23, 27, 23, 37); g.FillEllipse(Brushes.White, 40, 28, 5, 5); g.FillEllipse(Brushes.White, 48, 28, 5, 5);
            }
            bmp.Save(path, ImageFormat.Png); return path;
        }
        catch { return ""; }
    }

    static void Save(List<GameProfile> profiles) => File.WriteAllText(ProfilesFile, JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true }));
}

static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF r, float radius) { using var path = Rounded(r, radius); g.FillPath(brush, path); }
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle r, int radius) { using var path = Rounded(r, radius); g.DrawPath(pen, path); }
    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); var d = radius * 2; p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
    }
}
