using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;

namespace CrossFireRouteLab;

/// <summary>
/// Fixes the v10 measurement flaw: ICMP latency is not necessarily the latency
/// CrossFire reports. For TCP endpoints already opened by the game, GRL measures
/// TCP connect RTT and ranks every public candidate instead of taking the first
/// netstat row. It changes only GRL's measurement target; it never rewrites the
/// game's server selection or invents a route Windows does not have.
/// </summary>
internal static class EndpointMeasurementPatch
{
    static System.Threading.Timer? timer;
    static bool running;
    static string lastTarget = "";
    static double lastScore = -1;

    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (form.GetType().GetField("pingTimer", flags)?.GetValue(form) is System.Windows.Forms.Timer oldTimer)
            oldTimer.Stop();

        timer = new System.Threading.Timer(_ => Tick(form), null, 3500, 3500);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[AI] Endpoint engine enabled: ranking the game's actual public connections instead of trusting ICMP alone.");
    }

    static void Tick(Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();
        if (type.GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        if (type.GetField("gameName", flags)?.GetValue(form) is not string gameName || string.IsNullOrWhiteSpace(gameName)) return;
        if (type.GetField("connections", flags)?.GetValue(form) is not System.Collections.IEnumerable raw) return;

        var candidates = new List<Candidate>();
        foreach (var item in raw)
        {
            if (item == null) continue;
            var t = item.GetType();
            var ip = t.GetProperty("Ip")?.GetValue(item)?.ToString();
            var protocol = t.GetProperty("Protocol")?.GetValue(item)?.ToString() ?? "";
            var portObj = t.GetProperty("Port")?.GetValue(item);
            if (!int.TryParse(portObj?.ToString(), out var port) || string.IsNullOrWhiteSpace(ip)) continue;
            if (!IsPublic(ip) || port <= 0 || port > 65535) continue;
            candidates.Add(new Candidate(ip, port, protocol));
        }
        if (candidates.Count == 0) return;

        running = true;
        _ = Task.Run(() => RankAndPublish(form, gameName, candidates));
    }

    static async Task RankAndPublish(Form form, string gameName, List<Candidate> candidates)
    {
        try
        {
            var ranked = new List<Result>();
            foreach (var c in candidates.Take(12))
            {
                var samples = c.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)
                    ? await TcpSamples(c.Ip, c.Port, 3)
                    : await IcmpSamples(c.Ip, 3);
                var good = samples.Where(x => x >= 0).OrderBy(x => x).ToList();
                if (good.Count == 0) continue;
                var median = good[good.Count / 2];
                var average = good.Average();
                var loss = 1.0 - good.Count / 3.0;
                ranked.Add(new Result(c, median, average, loss));
            }

            if (ranked.Count == 0) return;
            var best = ranked.OrderBy(x => x.Median).ThenBy(x => x.Average).First();
            var hasCurrent = ranked.Any(x => $"{x.C.Ip}:{x.C.Port}".Equals(lastTarget, StringComparison.OrdinalIgnoreCase));
            if (hasCurrent)
            {
                var current = ranked.First(x => $"{x.C.Ip}:{x.C.Port}".Equals(lastTarget, StringComparison.OrdinalIgnoreCase));
                if (current.Median <= best.Median + 2.0) best = current;
            }

            Publish(form, gameName, best, ranked);
        }
        catch (Exception ex)
        {
            Log(form, "[AI] Endpoint engine stopped safely: " + ex.Message);
        }
        finally { running = false; }
    }

    static void Publish(Form form, string gameName, Result best, List<Result> ranked)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        form.BeginInvoke((Action)(() =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = form.GetType();
                type.GetField("endpoint", flags)?.SetValue(form, best.C.Ip);
                type.GetField("endpointPort", flags)?.SetValue(form, best.C.Port);
                type.GetField("lastPing", flags)?.SetValue(form, best.Median);
                type.GetField("jitter", flags)?.SetValue(form, 0.0);
                if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box)
                    box.Text = $"{best.C.Ip}:{best.C.Port}";

                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                {
                    metrics.Text = $"ENDPOINT   {best.C.Ip}:{best.C.Port}\r\n" +
                                   $"PROTOCOL   {best.C.Protocol}\r\n" +
                                   $"LATENCY    {best.Median:0} ms\r\n" +
                                   $"LOSS       {(best.Loss * 100):0.#}%\r\n" +
                                   $"JITTER     —\r\n" +
                                   $"STABILITY  {Stability(best.Median, best.Loss)}\r\n\r\n" +
                                   (best.C.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)
                                       ? "* TCP connect RTT is the transport probe."
                                       : "* UDP endpoint: ICMP is supporting evidence only.");
                }

                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                {
                    quality.Text = $"● LIVE • {best.Median:0} ms • {ranked.Count} CANDIDATE(S)";
                    quality.ForeColor = Color.FromArgb(40, 242, 122);
                }

                var graph = type.GetField("graph", flags)?.GetValue(form);
                var valuesProperty = graph?.GetType().GetProperty("Values", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                valuesProperty?.SetValue(graph, new[] { best.Median });

                var changed = !string.Equals(lastTarget, $"{best.C.Ip}:{best.C.Port}", StringComparison.OrdinalIgnoreCase) || Math.Abs(lastScore - best.Median) >= 2;
                lastTarget = $"{best.C.Ip}:{best.C.Port}";
                lastScore = best.Median;

                Log(form, $"[ENDPOINT AI] {gameName}: best exposed game connection = {best.C.Ip}:{best.C.Port} {best.C.Protocol} | median {best.Median:0} ms | candidates {ranked.Count}.");
                if (changed)
                    Log(form, $"[ENDPOINT AI] Measurement target updated to {lastTarget}. This does not force CrossFire to switch servers; it selects the best endpoint that CrossFire is already connected to.");
                if (ranked.Count == 1)
                    Log(form, "[ENDPOINT AI] Only one public game endpoint is exposed. There is no second server endpoint for GRL to switch to locally.");
            }
            catch { }
        }));
    }

    static async Task<List<double>> TcpSamples(string ip, int port, int count)
    {
        var list = new List<double>();
        for (var i = 0; i < count; i++)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                var sw = Stopwatch.StartNew();
                var task = client.ConnectAsync(ip, port);
                if (await Task.WhenAny(task, Task.Delay(1000)) == task && client.Connected)
                {
                    sw.Stop(); list.Add(sw.Elapsed.TotalMilliseconds);
                }
                else list.Add(-1);
            }
            catch { list.Add(-1); }
            await Task.Delay(60);
        }
        return list;
    }

    static async Task<List<double>> IcmpSamples(string ip, int count)
    {
        var list = new List<double>();
        for (var i = 0; i < count; i++)
        {
            try
            {
                using var ping = new Ping();
                var r = await ping.SendPingAsync(ip, 900);
                list.Add(r.Status == IPStatus.Success ? r.RoundtripTime : -1);
            }
            catch { list.Add(-1); }
        }
        return list;
    }

    static bool IsPublic(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return false;
        if (IPAddress.IsLoopback(a)) return false;
        var b = a.GetAddressBytes();
        if (b.Length != 4) return !a.IsIPv6LinkLocal;
        return !(b[0] == 10 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
    }

    static string Stability(double latency, double loss)
    {
        if (loss >= .34) return "POOR";
        if (latency <= 50) return "EXCELLENT";
        if (latency <= 80) return "GOOD";
        if (latency <= 120) return "FAIR";
        return "HIGH";
    }

    static void Log(Form form, string text)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try { form.BeginInvoke((Action)(() => form.GetType().GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }

    readonly record struct Candidate(string Ip, int Port, string Protocol);
    readonly record struct Result(Candidate C, double Median, double Average, double Loss);
}
