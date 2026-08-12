using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;

namespace CrossFireRouteLab;

/// <summary>
/// Transport-aware endpoint measurement.
///
/// For CrossFire, only room/game sockets are eligible for "best endpoint"
/// selection. Launcher/CDN HTTPS sockets (especially TCP/443) are excluded
/// whenever a room socket is visible. Measurements are TCP connect RTTs to
/// the actual exposed room endpoint; the UI does not call those values the
/// game's scoreboard ping.
/// </summary>
internal static class EndpointMeasurementPatch
{
    static System.Threading.Timer? timer;
    static bool running;
    static string lastTarget = "";
    static double lastScore = -1;

    static readonly HashSet<int> CrossFirePreferredPorts = new() { 10009, 13008 };
    static readonly HashSet<int> WebOrControlPorts = new() { 80, 443, 8080, 8443 };

    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (form.GetType().GetField("pingTimer", flags)?.GetValue(form) is System.Windows.Forms.Timer oldTimer)
            oldTimer.Stop();

        timer = new System.Threading.Timer(_ => Tick(form), null, 2500, 3500);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[AI] Endpoint engine enabled: ranking the game's exposed transport connections; CrossFire CDN/HTTPS sockets are excluded from room ranking.");
    }

    static void Tick(Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();

        // AutoAnalyze starts the legacy 1-second timer after its first probe.
        // Keep that timer stopped so it cannot replace the transport measurement
        // with an unrelated ICMP result a moment later.
        if (type.GetField("pingTimer", flags)?.GetValue(form) is System.Windows.Forms.Timer oldTimer)
            oldTimer.Stop();

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

        candidates = FilterCandidates(gameName, candidates);
        if (candidates.Count == 0) return;

        running = true;
        _ = Task.Run(() => RankAndPublish(form, gameName, candidates));
    }

    static List<Candidate> FilterCandidates(string gameName, List<Candidate> candidates)
    {
        if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return candidates;

        var tcp = candidates.Where(c => c.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)).ToList();
        var preferred = tcp.Where(c => CrossFirePreferredPorts.Contains(c.Port)).ToList();
        if (preferred.Count > 0) return preferred;

        var nonWeb = tcp.Where(c => !WebOrControlPorts.Contains(c.Port)).ToList();
        if (nonWeb.Count > 0) return nonWeb;

        // If the only visible CrossFire sockets are HTTPS/control sockets, keep
        // them visible to diagnostics but do not pretend they are room ping.
        return new List<Candidate>();
    }

    static async Task RankAndPublish(Form form, string gameName, List<Candidate> candidates)
    {
        try
        {
            var ranked = new List<Result>();
            foreach (var c in candidates.Take(12))
            {
                // A CrossFire candidate is always TCP here. For other games the
                // original ICMP-vs-TCP behavior is retained.
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
                    var isCrossFire = gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase);
                    metrics.Text = $"ENDPOINT   {best.C.Ip}:{best.C.Port}\r\n" +
                                   $"PROTOCOL   {best.C.Protocol}\r\n" +
                                   $"LATENCY    {best.Median:0} ms\r\n" +
                                   $"LOSS       {(best.Loss * 100):0.#}%\r\n" +
                                   $"JITTER     —\r\n" +
                                   $"STABILITY  {Stability(best.Median, best.Loss)}\r\n\r\n" +
                                   (isCrossFire
                                       ? "* Room TCP connect RTT; not the scoreboard's game-ping value."
                                       : (best.C.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)
                                           ? "* TCP connect RTT is the transport probe."
                                           : "* ICMP is supporting evidence only."));
                }

                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                {
                    quality.Text = $"● LIVE • {best.Median:0} ms • {ranked.Count} ROOM CANDIDATE(S)";
                    quality.ForeColor = Color.FromArgb(40, 242, 122);
                }

                var graph = type.GetField("graph", flags)?.GetValue(form);
                var valuesProperty = graph?.GetType().GetProperty("Values", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                valuesProperty?.SetValue(graph, new[] { best.Median });

                var changed = !string.Equals(lastTarget, $"{best.C.Ip}:{best.C.Port}", StringComparison.OrdinalIgnoreCase) || Math.Abs(lastScore - best.Median) >= 2;
                lastTarget = $"{best.C.Ip}:{best.C.Port}";
                lastScore = best.Median;

                Log(form, $"[ENDPOINT AI] {gameName}: best exposed room connection = {best.C.Ip}:{best.C.Port} {best.C.Protocol} | TCP median {best.Median:0} ms | candidates {ranked.Count}.");
                if (changed)
                    Log(form, $"[ENDPOINT AI] Measurement target updated to {lastTarget}. This measures a room socket CrossFire is already using; it does not force CrossFire to switch servers.");
                if (ranked.Count == 1)
                    Log(form, "[ENDPOINT AI] Only one public room endpoint is exposed right now. A CDN/HTTPS socket is not counted as a second game server.");
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
