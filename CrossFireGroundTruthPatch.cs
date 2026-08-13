using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// Reads the ping that CrossFire itself displays in the scoreboard.
/// This is deliberately separate from TCP path measurements: the number
/// shown by CrossFire is the ground-truth game latency used by route AI.
/// No UDP, ICMP, packet capture, or synthetic network probe is used here.
/// </summary>
public static class CrossFireGroundTruthPatch
{
    private static System.Threading.Timer? timer;
    private static int running;
    private static int lastAccepted = -1;
    private static readonly Queue<int> recent = new();

    private static readonly Dictionary<char, string[]> DigitTemplates = new()
    {
        ['0'] = new[] { ".#####...", ".##...##.", "##.....##", "##.....##", "##.....##", "##.....##", "##.....##", "##.....##", "##.....##", "##.....##", ".##...##.", "..#####.." },
        ['1'] = new[] { "..##..", "..##..", "####..", "..##..", "..##..", "..##..", "..##..", "..##..", "..##..", "..##..", "..##..", "######" },
        ['2'] = new[] { ".######..", "##....##.", ".......##", ".......##", ".......##", "......##.", ".....##..", "....##...", "...##....", "..##.....", ".##......", "#########" },
        ['3'] = new[] { ".######..", "##....##.", ".......##", ".......##", "......##.", "...####..", "......##.", ".......##", ".......##", ".......##", "##....##.", ".######.." },
        ['4'] = new[] { "......##..", ".....###..", "....####..", "...##.##..", "..##..##..", ".##...##..", "##....##..", "##....##..", "##########", "......##..", "......##..", "......##.." },
        ['5'] = new[] { ".########", ".##......", ".##......", ".##......", ".######..", "......##.", ".......##", ".......##", ".......##", ".......##", "##....##.", ".######.." },
        ['6'] = new[] { "...#####.", "..##.....", ".##......", "##.......", "#######..", "###...##.", "##.....##", "##.....##", "##.....##", "##.....##", ".##...##.", "..#####.." },
        ['7'] = new[] { "#########", ".......##", "......##.", "......##.", ".....##..", ".....##..", "....##...", "....##...", "...##....", "...##....", "..##.....", "..##....." },
        ['8'] = new[] { "..#####..", ".##...##.", "##.....##", "##.....##", ".##...##.", "..#####..", ".##...##.", "##.....##", "##.....##", "##.....##", ".##...##.", "..#####.." },
        ['9'] = new[] { "..#####..", ".##...##.", "##.....##", "##.....##", "##.....##", "##.....##", ".##...###", "..#######", ".......##", "......##.", ".....##..", ".#####..." }
    };

    public static void Apply(Form form)
    {
        timer?.Dispose();
        lastAccepted = -1;
        recent.Clear();
        StopTimer(form, "pingTimer");
        timer = new System.Threading.Timer(_ => Tick(form), null, 700, 700);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Write(form, "[CROSSFIRE] Actual game-ping reader enabled. Source: CrossFire scoreboard. UDP/ICMP probes are not used.");
    }

    /// <summary>Build-time regression check for the embedded digit recognizer.</summary>
    public static bool RecognizerSelfTest()
    {
        foreach (var pair in DigitTemplates)
        {
            var bitmap = TemplateToMask(pair.Value);
            var (digit, score) = RecognizeMask(bitmap);
            if (digit != pair.Key || score < 0.98) return false;
        }
        return true;
    }

    private static void Tick(Form form)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) return;
        try
        {
            StopTimer(form, "pingTimer");
            using var process = FindCrossFire();
            if (process == null || process.HasExited || process.MainWindowHandle == IntPtr.Zero)
            {
                PublishWaiting(form, "CROSSFIRE PING  — ms", "OPEN SCOREBOARD");
                return;
            }

            using var frame = CaptureClient(process.MainWindowHandle);
            if (frame == null)
            {
                PublishWaiting(form, "CROSSFIRE PING  — ms", "SCOREBOARD NOT VISIBLE");
                return;
            }

            var result = ReadScoreboardPing(frame);
            if (!result.Success)
            {
                PublishWaiting(form, "CROSSFIRE PING  — ms", result.Reason);
                return;
            }

            recent.Enqueue(result.PingMs);
            while (recent.Count > 5) recent.Dequeue();
            lastAccepted = result.PingMs;
            Publish(form, result.PingMs, result.Confidence, result.RowBounds);
        }
        catch (Exception ex)
        {
            Write(form, "[CROSSFIRE] Scoreboard reader safe error: " + ex.Message);
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
        if (!GetClientRect(hwnd, out var client) || client.Right <= client.Left || client.Bottom <= client.Top) return null;
        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(hwnd, ref origin)) return null;
        var size = new Size(client.Right - client.Left, client.Bottom - client.Top);
        try
        {
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(origin.X, origin.Y, 0, 0, size, CopyPixelOperation.SourceCopy);
            return bmp;
        }
        catch { return null; }
    }

    private static ReadResult ReadScoreboardPing(Bitmap bmp)
    {
        var width = bmp.Width;
        var height = bmp.Height;
        if (width < 800 || height < 500) return ReadResult.Fail("GAME WINDOW TOO SMALL");

        var lines = FindGreenHorizontalLines(bmp);
        if (lines.Count < 2) return ReadResult.Fail("SCOREBOARD NOT VISIBLE");

        (int y1, int y2, int left, int right)? best = null;
        var bestScore = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            for (int j = i + 1; j < lines.Count; j++)
            {
                var a = lines[i]; var b = lines[j];
                var gap = b.Y - a.Y;
                if (gap < 12 || gap > Math.Max(55, height / 18)) continue;
                var overlapLeft = Math.Max(a.Left, b.Left);
                var overlapRight = Math.Min(a.Right, b.Right);
                var overlap = overlapRight - overlapLeft;
                if (overlap < width * .28) continue;
                var score = overlap * 10 - Math.Abs(gap - height * .023);
                if (score > bestScore)
                {
                    bestScore = (int)score;
                    best = (a.Y, b.Y, overlapLeft, overlapRight);
                }
            }
        }

        if (best is null) return ReadResult.Fail("OWN PLAYER ROW NOT FOUND");
        var row = best.Value;
        var rowHeight = row.y2 - row.y1;
        var x0 = row.left + (int)(row.right - row.left * 0.0 + (row.right - row.left) * .875);
        var x1 = row.left + (int)((row.right - row.left) * .935);
        var y0 = Math.Max(0, row.y1 + Math.Max(2, rowHeight / 7));
        var y1 = Math.Min(height - 1, row.y2 - Math.Max(2, rowHeight / 7));
        if (x1 <= x0 || y1 <= y0) return ReadResult.Fail("PING REGION INVALID");

        var mask = BuildDigitMask(bmp, x0, y0, x1, y1);
        var components = Components(mask);
        var digits = new List<(int X, char Digit, double Score)>();
        foreach (var component in components)
        {
            if (component.Width < 4 || component.Height < 7 || component.Width > 18 || component.Height > 30 || component.Area < 15) continue;
            var local = Crop(mask, component);
            var (digit, score) = RecognizeMask(local);
            if (digit != '?' && score >= .48) digits.Add((component.X, digit, score));
        }

        if (digits.Count < 1 || digits.Count > 3) return ReadResult.Fail("PING DIGITS NOT RECOGNIZED");
        digits.Sort((a, b) => a.X.CompareTo(b.X));
        var text = new string(digits.Select(x => x.Digit).ToArray());
        if (!int.TryParse(text, out var ping) || ping < 1 || ping > 999) return ReadResult.Fail("PING VALUE INVALID");
        var confidence = digits.Average(x => x.Score);
        return new ReadResult(true, ping, confidence, (row.left, row.y1, row.right, row.y2), "");
    }

    private static List<GreenLine> FindGreenHorizontalLines(Bitmap bmp)
    {
        var result = new List<GreenLine>();
        var startX = Math.Max(0, (int)(bmp.Width * .15));
        var endX = Math.Min(bmp.Width - 1, (int)(bmp.Width * .85));
        for (var y = (int)(bmp.Height * .12); y < bmp.Height * .86; y++)
        {
            var bestRun = 0; var bestLeft = 0; var run = 0; var runLeft = 0;
            for (var x = startX; x <= endX; x++)
            {
                var c = bmp.GetPixel(x, y);
                var good = c.G > 65 && c.G > c.R * 1.10 && c.G > c.B * 1.04;
                if (good)
                {
                    if (run == 0) runLeft = x;
                    run++;
                    if (run > bestRun) { bestRun = run; bestLeft = runLeft; }
                }
                else run = 0;
            }
            if (bestRun >= bmp.Width * .28) result.Add(new GreenLine(y, bestLeft, bestLeft + bestRun - 1));
        }

        var clustered = new List<GreenLine>();
        foreach (var line in result)
        {
            if (clustered.Count == 0 || line.Y - clustered[^1].Y > 2) clustered.Add(line);
            else if (line.Right - line.Left > clustered[^1].Right - clustered[^1].Left) clustered[^1] = line;
        }
        return clustered;
    }

    private static bool[,] BuildDigitMask(Bitmap bmp, int x0, int y0, int x1, int y1)
    {
        var w = x1 - x0 + 1; var h = y1 - y0 + 1;
        var mask = new bool[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x0 + x, y0 + y);
                var brightness = (c.R + c.G + c.B) / 3;
                mask[y, x] = brightness >= 82 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 105;
            }
        }
        return mask;
    }

    private static List<Component> Components(bool[,] mask)
    {
        var h = mask.GetLength(0); var w = mask.GetLength(1);
        var seen = new bool[h, w]; var result = new List<Component>();
        var q = new Queue<(int X, int Y)>();
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            if (!mask[y, x] || seen[y, x]) continue;
            seen[y, x] = true; q.Enqueue((x, y));
            var pixels = new List<(int X, int Y)>();
            var minX = x; var maxX = x; var minY = y; var maxY = y;
            while (q.Count > 0)
            {
                var p = q.Dequeue(); pixels.Add(p);
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
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

    private static (char Digit, double Score) RecognizeMask(bool[,] mask)
    {
        var bestDigit = '?'; var best = 0.0;
        foreach (var pair in DigitTemplates)
        {
            var template = TemplateToMask(pair.Value);
            var score = Similarity(mask, template);
            if (score > best) { best = score; bestDigit = pair.Key; }
        }
        return (bestDigit, best);
    }

    private static double Similarity(bool[,] source, bool[,] template)
    {
        var resized = ResizeMask(source, template.GetLength(1), template.GetLength(0));
        var best = 0.0;
        for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
        {
            var intersection = 0; var union = 0;
            for (int y = 0; y < template.GetLength(0); y++) for (int x = 0; x < template.GetLength(1); x++)
            {
                var sx = x + dx; var sy = y + dy;
                var s = sx >= 0 && sy >= 0 && sx < template.GetLength(1) && sy < template.GetLength(0) && resized[sy, sx];
                var t = template[y, x];
                if (s && t) intersection++;
                if (s || t) union++;
            }
            best = Math.Max(best, union == 0 ? 0 : (double)intersection / union);
        }
        return best;
    }

    private static bool[,] ResizeMask(bool[,] source, int width, int height)
    {
        var result = new bool[height, width];
        var sw = source.GetLength(1); var sh = source.GetLength(0);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var sx0 = x * sw / width; var sx1 = Math.Max(sx0 + 1, (x + 1) * sw / width);
            var sy0 = y * sh / height; var sy1 = Math.Max(sy0 + 1, (y + 1) * sh / height);
            var count = 0; var total = 0;
            for (int sy = sy0; sy < Math.Min(sh, sy1); sy++) for (int sx = sx0; sx < Math.Min(sw, sx1); sx++) { if (source[sy, sx]) count++; total++; }
            result[y, x] = total > 0 && count * 2 >= total;
        }
        return result;
    }

    private static bool[,] TemplateToMask(string[] lines)
    {
        var h = lines.Length; var w = lines.Max(x => x.Length); var m = new bool[h, w];
        for (int y = 0; y < h; y++) for (int x = 0; x < lines[y].Length; x++) m[y, x] = lines[y][x] == '#';
        return m;
    }

    private static void Publish(Form form, int ping, double confidence, (int Left, int Top, int Right, int Bottom) row)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                var metrics = type.GetField("metrics", flags)?.GetValue(form) as Label;
                var quality = type.GetField("quality", flags)?.GetValue(form) as Label;
                var console = type.GetField("console", flags)?.GetValue(form) as RichTextBox;
                var endpoint = type.GetField("endpoint", flags)?.GetValue(form) as string;
                var endpointPort = (int?)type.GetField("endpointPort", flags)?.GetValue(form) ?? 0;
                var tcpProbe = (double?)type.GetField("lastPing", flags)?.GetValue(form) ?? -1;
                var graph = type.GetField("graph", flags)?.GetValue(form) as Control;

                if (metrics != null)
                    metrics.Text = $"CROSSFIRE PING  {ping} ms\r\nSOURCE          SCOREBOARD\r\nCONFIDENCE      {confidence:P0}\r\nTCP TARGET      {(string.IsNullOrWhiteSpace(endpoint) ? "—" : $"{endpoint}:{endpointPort}")}\r\nTCP PROBE RTT   {(tcpProbe >= 0 ? $"{tcpProbe:0.0} ms" : "—")}";
                if (quality != null) { quality.Text = $"● CROSSFIRE • ACTUAL {ping} ms"; quality.ForeColor = Color.FromArgb(40, 242, 122); }
                if (graph is not null) graph.Invalidate();
                if (console != null && (lastAccepted < 0 || lastAccepted != ping))
                {
                    console.AppendText($"[{DateTime.Now:HH:mm:ss}] [CROSSFIRE] Actual room ping → {ping} ms | scoreboard confidence {confidence:P0}\r\n");
                    console.SelectionStart = console.TextLength; console.ScrollToCaret();
                }
            }));
        }
        catch { }
    }

    private static void PublishWaiting(Form form, string metricsText, string reason)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics) metrics.Text = metricsText + $"\r\nSOURCE          SCOREBOARD\r\nSTATUS          {reason}";
                if (type.GetField("quality", flags)?.GetValue(form) is Label quality) { quality.Text = "● CROSSFIRE • WAITING FOR SCOREBOARD"; quality.ForeColor = Color.FromArgb(132, 157, 190); }
            }));
        }
        catch { }
    }

    private static void StopTimer(Form form, string name)
    {
        try
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            (form.GetType().GetField(name, flags)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop();
        }
        catch { }
    }

    private static void Write(Form form, string message)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                if (form.GetType().GetField("console", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(form) is RichTextBox console)
                {
                    console.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
                    console.SelectionStart = console.TextLength; console.ScrollToCaret();
                }
            }));
        }
        catch { }
    }

    private sealed record GreenLine(int Y, int Left, int Right);
    private sealed record Component(int X, int Y, int Width, int Height, List<(int X, int Y)> Pixels)
    {
        public int Area => Pixels.Count;
    }
    private sealed record ReadResult(bool Success, int PingMs, double Confidence, (int Left, int Top, int Right, int Bottom) RowBounds, string Reason)
    {
        public static ReadResult Fail(string reason) => new(false, -1, 0, default, reason);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
}
