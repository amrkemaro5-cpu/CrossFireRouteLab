using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Reflection;

namespace CrossFireRouteLab;

/// <summary>
/// Ground-truth CrossFire room latency reader.
/// It reads the player's highlighted Ping cell from the visible scoreboard.
/// It does not send traffic and does not inspect UDP packets.
/// </summary>
public static class CrossFireScoreboardPingReader
{
    private static System.Threading.Timer? timer;
    private static int running;
    private static int lastPing = -1;

    // 5x7 normalized glyphs taken from the CrossFire scoreboard font visible
    // in the supplied test screenshot. This avoids a heavyweight OCR package.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { "01110", "10001", "10001", "10001", "10001", "10001", "01110" },
        ['1'] = new[] { "00100", "11100", "00100", "00100", "00100", "00100", "11111" },
        ['2'] = new[] { "11110", "00001", "00001", "00010", "00100", "01000", "11111" },
        ['3'] = new[] { "11110", "00001", "00001", "00010", "00001", "00001", "11110" },
        ['4'] = new[] { "00010", "00110", "01010", "10010", "10010", "00010", "00010" },
        ['5'] = new[] { "01111", "01000", "01110", "00001", "00001", "00001", "11110" },
        ['6'] = new[] { "01110", "11000", "11110", "10001", "10001", "10001", "01110" },
        ['7'] = new[] { "11111", "00011", "00010", "00010", "00100", "00100", "01000" },
        ['8'] = new[] { "01110", "10001", "10001", "01010", "10001", "10001", "01110" },
        ['9'] = new[] { "01110", "10001", "10001", "10001", "01111", "00011", "01110" }
    };

    public static void Apply(Form form)
    {
        timer?.Dispose();
        lastPing = -1;
        StopPingTimer(form);
        timer = new System.Threading.Timer(_ => Tick(form), null, 700, 700);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[CROSSFIRE] Actual room-ping reader enabled. Source = CrossFire scoreboard. No UDP/ICMP probe.");
    }

    public static bool RecognizerSelfTest()
    {
        foreach (var pair in Glyphs)
        {
            var mask = new bool[7, 5];
            for (var y = 0; y < 7; y++) for (var x = 0; x < 5; x++) mask[y, x] = pair.Value[y][x] == '1';
            var (digit, score) = Recognize(mask);
            if (digit != pair.Key || score < .99) return false;
        }
        return true;
    }

    private static void Tick(Form form)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) return;
        try
        {
            StopPingTimer(form);
            using var game = FindCrossFire();
            if (game == null || game.HasExited || game.MainWindowHandle == IntPtr.Zero)
            {
                PublishWaiting(form, "OPEN SCOREBOARD");
                return;
            }

            using var frame = CaptureClient(game.MainWindowHandle);
            if (frame == null)
            {
                PublishWaiting(form, "SCOREBOARD NOT VISIBLE");
                return;
            }

            var result = ReadPing(frame);
            if (!result.Success)
            {
                PublishWaiting(form, result.Reason);
                return;
            }

            var changed = result.Ping != lastPing;
            lastPing = result.Ping;
            Publish(form, result.Ping, result.Confidence, changed);
        }
        catch (Exception ex)
        {
            Log(form, "[CROSSFIRE] Scoreboard reader error: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
        }
    }

    private static Process? FindCrossFire()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch { p.Dispose(); }
        }
        return null;
    }

    private static Bitmap? CaptureClient(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) return null;
        var pt = new POINT();
        if (!ClientToScreen(hwnd, ref pt)) return null;
        var size = new Size(r.Right - r.Left, r.Bottom - r.Top);
        try
        {
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(pt.X, pt.Y, 0, 0, size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch { return null; }
    }

    private static ReadResult ReadPing(Bitmap bmp)
    {
        if (bmp.Width < 800 || bmp.Height < 500) return ReadResult.Fail("GAME WINDOW TOO SMALL");
        var lines = FindGreenLines(bmp);
        if (lines.Count < 2) return ReadResult.Fail("SCOREBOARD NOT VISIBLE");

        GreenLine? top = null; GreenLine? bottom = null; double best = double.MinValue;
        for (var i = 0; i < lines.Count; i++) for (var j = i + 1; j < lines.Count; j++)
        {
            var a = lines[i]; var b = lines[j];
            var gap = b.Y - a.Y;
            if (gap < 12 || gap > Math.Max(55, bmp.Height / 18)) continue;
            var left = Math.Max(a.Left, b.Left); var right = Math.Min(a.Right, b.Right);
            var overlap = right - left;
            if (overlap < bmp.Width * .28) continue;
            var score = overlap * 10.0 - Math.Abs(gap - bmp.Height * .023);
            if (score > best) { best = score; top = a; bottom = b; }
        }

        if (top == null || bottom == null) return ReadResult.Fail("OWN PLAYER ROW NOT FOUND");
        var rowLeft = Math.Max(top.Left, bottom.Left);
        var rowRight = Math.Min(top.Right, bottom.Right);
        var rowWidth = rowRight - rowLeft;
        var rowHeight = bottom.Y - top.Y;
        var x0 = rowLeft + (int)(rowWidth * .875);
        var x1 = rowLeft + (int)(rowWidth * .935);
        var y0 = top.Y + Math.Max(2, rowHeight / 7);
        var y1 = bottom.Y - Math.Max(2, rowHeight / 7);
        if (x1 <= x0 || y1 <= y0) return ReadResult.Fail("PING REGION INVALID");

        var mask = BuildMask(bmp, x0, y0, x1, y1);
        var components = FindComponents(mask);
        var digits = new List<(int X, char Digit, double Score)>();
        foreach (var c in components)
        {
            if (c.Width < 4 || c.Height < 7 || c.Width > 18 || c.Height > 30 || c.Area < 12) continue;
            var glyph = Crop(mask, c);
            var (digit, confidence) = Recognize(glyph);
            if (digit != '?' && confidence >= .55) digits.Add((c.X, digit, confidence));
        }

        if (digits.Count is < 1 or > 3) return ReadResult.Fail("PING DIGITS NOT RECOGNIZED");
        digits.Sort((a, b) => a.X.CompareTo(b.X));
        var text = new string(digits.Select(d => d.Digit).ToArray());
        if (!int.TryParse(text, out var ping) || ping < 1 || ping > 999) return ReadResult.Fail("PING VALUE INVALID");
        return new ReadResult(true, ping, digits.Average(d => d.Score), "");
    }

    private static List<GreenLine> FindGreenLines(Bitmap bmp)
    {
        var lines = new List<GreenLine>();
        var startX = (int)(bmp.Width * .15); var endX = (int)(bmp.Width * .85);
        for (var y = (int)(bmp.Height * .12); y < bmp.Height * .86; y++)
        {
            var run = 0; var runLeft = 0; var bestRun = 0; var bestLeft = 0;
            for (var x = startX; x <= endX; x++)
            {
                var c = bmp.GetPixel(x, y);
                var green = c.G > 65 && c.G > c.R * 1.10 && c.G > c.B * 1.04;
                if (green)
                {
                    if (run == 0) runLeft = x;
                    run++;
                    if (run > bestRun) { bestRun = run; bestLeft = runLeft; }
                }
                else run = 0;
            }
            if (bestRun >= bmp.Width * .28) lines.Add(new GreenLine(y, bestLeft, bestLeft + bestRun - 1));
        }

        var compact = new List<GreenLine>();
        foreach (var line in lines)
        {
            if (compact.Count == 0 || line.Y - compact[^1].Y > 2) compact.Add(line);
            else if (line.Right - line.Left > compact[^1].Right - compact[^1].Left) compact[^1] = line;
        }
        return compact;
    }

    private static bool[,] BuildMask(Bitmap bmp, int x0, int y0, int x1, int y1)
    {
        var mask = new bool[y1 - y0 + 1, x1 - x0 + 1];
        for (var y = 0; y < mask.GetLength(0); y++) for (var x = 0; x < mask.GetLength(1); x++)
        {
            var c = bmp.GetPixel(x0 + x, y0 + y);
            var brightness = (c.R + c.G + c.B) / 3;
            var spread = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
            mask[y, x] = brightness >= 82 && spread < 105;
        }
        return mask;
    }

    private static List<Component> FindComponents(bool[,] mask)
    {
        var h = mask.GetLength(0); var w = mask.GetLength(1); var seen = new bool[h, w]; var result = new List<Component>();
        var q = new Queue<(int X, int Y)>();
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        {
            if (!mask[y, x] || seen[y, x]) continue;
            seen[y, x] = true; q.Enqueue((x, y)); var pixels = new List<(int X, int Y)>();
            var minX = x; var maxX = x; var minY = y; var maxY = y;
            while (q.Count > 0)
            {
                var p = q.Dequeue(); pixels.Add(p);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = p.X + dx; var ny = p.Y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || seen[ny, nx] || !mask[ny, nx]) continue;
                    seen[ny, nx] = true; q.Enqueue((nx, ny));
                }
            }
            result.Add(new Component(minX, minY, maxX - minX + 1, maxY - minY + 1, pixels));
        }
        return result;
    }

    private static bool[,] Crop(bool[,] mask, Component c)
    {
        var output = new bool[c.Height, c.Width];
        foreach (var p in c.Pixels) output[p.Y - c.Y, p.X - c.X] = true;
        return output;
    }

    private static (char Digit, double Score) Recognize(bool[,] source)
    {
        var bestDigit = '?'; var best = 0.0;
        foreach (var pair in Glyphs)
        {
            var template = new bool[7, 5];
            for (var y = 0; y < 7; y++) for (var x = 0; x < 5; x++) template[y, x] = pair.Value[y][x] == '1';
            var score = Similarity(source, template);
            if (score > best) { best = score; bestDigit = pair.Key; }
        }
        return (bestDigit, best);
    }

    private static double Similarity(bool[,] source, bool[,] template)
    {
        var resized = Resize(source, 5, 7); var best = 0.0;
        for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++)
        {
            var intersection = 0; var union = 0;
            for (var y = 0; y < 7; y++) for (var x = 0; x < 5; x++)
            {
                var sx = x + dx; var sy = y + dy;
                var s = sx >= 0 && sy >= 0 && sx < 5 && sy < 7 && resized[sy, sx]; var t = template[y, x];
                if (s && t) intersection++;
                if (s || t) union++;
            }
            best = Math.Max(best, union == 0 ? 0 : (double)intersection / union);
        }
        return best;
    }

    private static bool[,] Resize(bool[,] source, int width, int height)
    {
        var result = new bool[height, width]; var sw = source.GetLength(1); var sh = source.GetLength(0);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var sx0 = x * sw / width; var sx1 = Math.Max(sx0 + 1, (x + 1) * sw / width);
            var sy0 = y * sh / height; var sy1 = Math.Max(sy0 + 1, (y + 1) * sh / height);
            var count = 0; var total = 0;
            for (var sy = sy0; sy < Math.Min(sh, sy1); sy++) for (var sx = sx0; sx < Math.Min(sw, sx1); sx++) { if (source[sy, sx]) count++; total++; }
            result[y, x] = total > 0 && count * 2 >= total;
        }
        return result;
    }

    private static void Publish(Form form, int ping, double confidence, bool changed)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic; var type = typeof(GameRouteLabV10Form);
                var metrics = type.GetField("metrics", flags)?.GetValue(form) as Label;
                var quality = type.GetField("quality", flags)?.GetValue(form) as Label;
                var console = type.GetField("console", flags)?.GetValue(form) as RichTextBox;
                var endpoint = type.GetField("endpoint", flags)?.GetValue(form) as string;
                var endpointPort = (int?)type.GetField("endpointPort", flags)?.GetValue(form) ?? 0;
                var tcpProbe = (double?)type.GetField("lastPing", flags)?.GetValue(form) ?? -1;
                if (metrics != null) metrics.Text = $"CROSSFIRE PING  {ping} ms\r\nSOURCE          SCOREBOARD\r\nCONFIDENCE      {confidence:P0}\r\nTCP TARGET      {(string.IsNullOrWhiteSpace(endpoint) ? "—" : $"{endpoint}:{endpointPort}")}\r\nTCP PROBE RTT   {(tcpProbe >= 0 ? $"{tcpProbe:0.0} ms" : "—")}";
                if (quality != null) { quality.Text = $"● CROSSFIRE • ACTUAL {ping} ms"; quality.ForeColor = Color.FromArgb(40, 242, 122); }
                if (changed && console != null) { console.AppendText($"[{DateTime.Now:HH:mm:ss}] [CROSSFIRE] Actual room ping → {ping} ms | scoreboard confidence {confidence:P0}\r\n"); console.SelectionStart = console.TextLength; console.ScrollToCaret(); }
            }));
        }
        catch { }
    }

    private static void PublishWaiting(Form form, string reason)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic; var type = typeof(GameRouteLabV10Form);
                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics) metrics.Text = $"CROSSFIRE PING  — ms\r\nSOURCE          SCOREBOARD\r\nSTATUS          {reason}";
                if (type.GetField("quality", flags)?.GetValue(form) is Label quality) { quality.Text = "● CROSSFIRE • WAITING FOR SCOREBOARD"; quality.ForeColor = Color.FromArgb(132, 157, 190); }
            }));
        }
        catch { }
    }

    private static void StopPingTimer(Form form)
    {
        try { (form.GetType().GetField("pingTimer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop(); } catch { }
    }

    private static void Log(Form form, string message)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                if (form.GetType().GetField("console", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) is RichTextBox console)
                { console.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n"); console.SelectionStart = console.TextLength; console.ScrollToCaret(); }
            }));
        }
        catch { }
    }

    private sealed record GreenLine(int Y, int Left, int Right);
    private sealed record Component(int X, int Y, int Width, int Height, List<(int X, int Y)> Pixels) { public int Area => Pixels.Count; }
    private sealed record ReadResult(bool Success, int Ping, double Confidence, string Reason) { public static ReadResult Fail(string reason) => new(false, -1, 0, reason); }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
}
