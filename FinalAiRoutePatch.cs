using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Final one-click evidence layer. It does not pretend that a single ISP path
/// can be shortened by software. It learns the best real room endpoints seen
/// over time, compares the current room against that history, and gives the
/// user a concrete room/server choice when a materially better one has been
/// observed. It also reports whether an alternate local interface exists.
/// </summary>
internal static class FinalAiRoutePatch
{
    static bool installed;
    static bool running;
    static System.Threading.Timer? timer;
    static readonly string Store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab", "crossfire-room-history.json");
    const double BetterMs = 5.0;

    public static void Apply(Form form)
    {
        if (installed || form.IsDisposed) return;
        installed = true;
        Install(form);
    }

    static void Install(Form form)
    {
        var auto = Find(form, "AUTO ANALYZE");
        if (auto != null)
        {
            auto.Click += async (_, _) =>
            {
                if (running) return;
                running = true;
                try { await Task.Delay(900); await Analyze(form); }
                catch (Exception ex) { Log(form, "[AI FINAL] " + ex.Message); }
                finally { running = false; }
            };
        }

        var header = form.Controls.Cast<Control>().FirstOrDefault(c => c.Controls.Cast<Control>().Any(x => x.Text == "GAME ROUTE LAB"));
        if (header != null)
        {
            var status = new Label { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Text = "AI BEST-ROUTE ENGINE • READY", ForeColor = Color.FromArgb(40, 242, 122), BackColor = Color.FromArgb(7, 13, 27), Bounds = new Rectangle(1220, 88, 240, 24), Font = new Font("Segoe UI Semibold", 8f) };
            header.Controls.Add(status);
        }

        timer = new System.Threading.Timer(_ => TryPassiveRecord(form), null, 15000, 10000);
        Log(form, "[AI FINAL] One-click final layer armed: current room + best observed room + alternate local path evidence.");
    }

    static async Task Analyze(Form form)
    {
        if (!IsCrossFire(form)) return;
        if (!TryRoom(out var ip, out var port, out var protocol))
        {
            Log(form, "[AI FINAL] No verified room endpoint yet. Stay inside the active room and run AUTO ANALYZE again.");
            return;
        }

        double current = -1;
        try { if (CrossFireRoomTransportProbeV3.TryGetPassiveRtt(out var passive, out var passiveSamples, out _) && passive >= 0 && passiveSamples > 0) current = passive; } catch { }
        if (current < 0) current = await Measure(ip, port, protocol, 5);
        Save(ip, port, protocol, current);
        var best = Load().Where(x => x.Latency >= 0).OrderBy(x => x.Latency).FirstOrDefault();

        Log(form, $"[AI FINAL] ROOM TARGET {ip}:{port} {protocol} → {(current < 0 ? "measurement unavailable" : current.ToString("0") + " ms")}.");
        if (best != null)
        {
            Log(form, $"[AI FINAL] BEST OBSERVED ROOM → {best.Ip}:{best.Port} {best.Protocol} @ {best.Latency:0} ms (samples {best.Samples}).");
            if (!Same(best, ip, port, protocol) && current >= 0 && best.Latency + BetterMs < current)
                Log(form, $"[AI FINAL] ACTION: this room is about {current - best.Latency:0} ms slower than the best room previously observed. Choose the room/server that produces {best.Ip}:{best.Port} if the game exposes it.");
            else if (Same(best, ip, port, protocol))
                Log(form, "[AI FINAL] ACTION: current room is the best stable room observed so far.");
            else
                Log(form, "[AI FINAL] ACTION: no materially better observed room exists yet; keep collecting active-room samples.");
        }

        await ReportLocalRoutes(form, ip, port, protocol);
    }

    static async Task ReportLocalRoutes(Form form, string ip, int port, string protocol)
    {
        var routes = await ReadRoutes();
        var usable = routes.Where(r => r.Up && r.Gateway.Length > 0).GroupBy(r => r.InterfaceIndex).Select(g => g.OrderBy(x => x.Metric).First()).ToList();
        if (usable.Count <= 1)
        {
            var only = usable.FirstOrDefault();
            Log(form, only.InterfaceIndex > 0
                ? $"[AI FINAL] LOCAL PATH: only {only.Alias} → {only.Gateway} is active. No local route switch can manufacture a 40–50 ms Internet path."
                : "[AI FINAL] LOCAL PATH: no alternate interface is available to benchmark.");
            return;
        }

        Log(form, $"[AI FINAL] LOCAL PATH: {usable.Count} active default interfaces detected; benchmarking the real room target.");
        foreach (var r in usable)
        {
            if (!TryAddRoute(ip, r)) { Log(form, $"[AI FINAL] SKIP {r.Alias}: Windows rejected temporary /32 test route."); continue; }
            try
            {
                var ms = await Measure(ip, port, protocol, 3);
                Log(form, $"[AI FINAL] PATH {r.Alias} → {r.Gateway}: {(ms < 0 ? "unreachable" : ms.ToString("0.0") + " ms")}.");
            }
            finally { RemoveRoute(ip); }
        }
    }

    static void TryPassiveRecord(Form form)
    {
        if (running || !IsCrossFire(form)) return;
        if (TryRoom(out var ip, out var port, out var protocol))
        {
            _ = Task.Run(async () =>
            {
                double ms = -1;
                try { if (CrossFireRoomTransportProbeV3.TryGetPassiveRtt(out var passive, out var samples, out _) && passive >= 0 && samples > 0) ms = passive; } catch { }
                if (ms < 0) ms = await Measure(ip, port, protocol, 3);
                Save(ip, port, protocol, ms);
            });
        }
    }

    static bool TryRoom(out string ip, out int port, out string protocol)
    {
        ip = ""; port = 0; protocol = "";
        try { return CrossFireRoomTransportProbeV3.TryGetTarget(out ip, out port, out protocol); }
        catch { return false; }
    }

    static async Task<double> Measure(string ip, int port, string protocol, int count)
    {
        var values = new List<double>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                if (protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
                {
                    using var c = new TcpClient { NoDelay = true };
                    var sw = Stopwatch.StartNew();
                    var t = c.ConnectAsync(ip, port);
                    if (await Task.WhenAny(t, Task.Delay(1200)) == t && c.Connected) { sw.Stop(); values.Add(sw.Elapsed.TotalMilliseconds); }
                }
                else
                {
                    using var p = new Ping();
                    var sw = Stopwatch.StartNew();
                    var r = await p.SendPingAsync(ip, 1200);
                    sw.Stop();
                    if (r.Status == IPStatus.Success) values.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            catch { }
            await Task.Delay(100);
        }
        return values.Count == 0 ? -1 : values.OrderBy(x => x).ElementAt(values.Count / 2);
    }

    static List<RoomSample> Load()
    {
        try
        {
            if (!File.Exists(Store)) return new();
            return JsonSerializer.Deserialize<List<RoomSample>>(File.ReadAllText(Store)) ?? new();
        }
        catch { return new(); }
    }

    static void Save(string ip, int port, string protocol, double latency)
    {
        if (latency < 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            var all = Load();
            var item = all.FirstOrDefault(x => Same(x, ip, port, protocol));
            if (item == null) all.Add(new RoomSample(ip, port, protocol, latency, 1, DateTime.UtcNow));
            else
            {
                var n = item.Samples;
                item.Latency = Math.Round((item.Latency * n + latency) / (n + 1), 1);
                item.Samples = n + 1;
                item.LastSeenUtc = DateTime.UtcNow;
            }
            all = all.OrderBy(x => x.Latency).Take(50).ToList();
            File.WriteAllText(Store, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    static bool Same(RoomSample x, string ip, int port, string protocol) => x.Ip == ip && x.Port == port && x.Protocol.Equals(protocol, StringComparison.OrdinalIgnoreCase);

    static async Task<List<DefaultRoute>> ReadRoutes()
    {
        const string cmd = "Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore ActiveStore | ForEach-Object { $a=Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue; [pscustomobject]@{InterfaceIndex=$_.InterfaceIndex;Alias=$a.Name;Gateway=$_.NextHop;Metric=$_.RouteMetric;Up=($a.Status -eq 'Up')} } | ConvertTo-Json -Compress";
        var output = await Run("powershell.exe", "-NoProfile -NonInteractive -Command " + Quote(cmd), 8000);
        var list = new List<DefaultRoute>();
        try
        {
            using var doc = JsonDocument.Parse(output.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : new[] { doc.RootElement };
            foreach (var x in items) list.Add(new DefaultRoute(ReadInt(x, "InterfaceIndex"), Read(x, "Alias"), Read(x, "Gateway"), ReadInt(x, "Metric"), x.TryGetProperty("Up", out var up) && up.GetBoolean()));
        }
        catch { }
        return list;
    }

    static bool TryAddRoute(string ip, DefaultRoute r)
    {
        var output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + Quote($"New-NetRoute -DestinationPrefix '{ip}/32' -InterfaceIndex {r.InterfaceIndex} -NextHop '{r.Gateway}' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null"), 6000).GetAwaiter().GetResult();
        return !output.Contains("Exception", StringComparison.OrdinalIgnoreCase) && !output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) && !output.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    static void RemoveRoute(string ip) => _ = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + Quote($"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{ $_.RouteMetric -eq 1 }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue"), 6000);
    static async Task<string> Run(string file, string args, int timeout) => await Task.Run(() => RunSync(file, args, timeout));
    static string RunSync(string file, string args, int timeout) { try { using var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } }; p.Start(); if (!p.WaitForExit(timeout)) { try { p.Kill(); } catch { } return "timeout"; } return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd(); } catch { return ""; } }
    static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
    static string Read(JsonElement x, string n) => x.TryGetProperty(n, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";
    static int ReadInt(JsonElement x, string n) => x.TryGetProperty(n, out var p) && p.TryGetInt32(out var v) ? v : 0;
    static bool IsCrossFire(Form f) => (f.GetType().GetField("gameName", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(f)?.ToString() ?? "").Contains("crossfire", StringComparison.OrdinalIgnoreCase);
    static Button? Find(Control root, string text) => All(root).OfType<Button>().FirstOrDefault(b => b.Text.Equals(text, StringComparison.OrdinalIgnoreCase));
    static IEnumerable<Control> All(Control root) { foreach (Control c in root.Controls) { yield return c; foreach (var n in All(c)) yield return n; } }
    static void Log(Form f, string s) { try { f.BeginInvoke((Action)(() => f.GetType().GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(f, new object[] { s }))); } catch { } }

    sealed class RoomSample
    {
        public string Ip { get; set; } = "";
        public int Port { get; set; }
        public string Protocol { get; set; } = "";
        public double Latency { get; set; }
        public int Samples { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public RoomSample() { }
        public RoomSample(string ip, int port, string protocol, double latency, int samples, DateTime last) { Ip = ip; Port = port; Protocol = protocol; Latency = latency; Samples = samples; LastSeenUtc = last; }
    }

    readonly record struct DefaultRoute(int InterfaceIndex, string Alias, string Gateway, int Metric, bool Up);
}
