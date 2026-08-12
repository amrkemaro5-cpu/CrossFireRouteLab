using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;

namespace CrossFireRouteLab;

/// <summary>
/// Measures the active transport endpoints discovered for a game.
/// For CrossFire, 10009/13008 are master/control sockets, not automatically the
/// room server. If a different active room TCP endpoint is visible, it is the
/// measurement target. Historical "SEEN ... AGO" sockets remain in the UI but
/// are not used as a live target.
/// </summary>
internal static class EndpointMeasurementPatch
{
    static System.Threading.Timer? timer;
    static bool running;
    static string lastTarget = "";
    static double lastScore = -1;

    static readonly HashSet<int> CrossFireMasterPorts = new() { 10009, 13008 };
    static readonly HashSet<int> WebOrControlPorts = new() { 80, 443, 8080, 8443 };

    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (form.GetType().GetField("pingTimer", flags)?.GetValue(form) is System.Windows.Forms.Timer oldTimer)
            oldTimer.Stop();
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 2200, 2200);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[AI] Endpoint engine fixed: CrossFire room TCP is measured separately from 10009/13008 master/control sockets.");
    }

    static void Tick(Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();
        if (type.GetField("pingTimer", flags)?.GetValue(form) is System.Windows.Forms.Timer oldTimer) oldTimer.Stop();
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
            var state = t.GetProperty("State")?.GetValue(item)?.ToString() ?? "";
            var portObj = t.GetProperty("Port")?.GetValue(item);
            if (!int.TryParse(portObj?.ToString(), out var port) || string.IsNullOrWhiteSpace(ip)) continue;
            if (!IsPublic(ip) || port <= 0 || port > 65535) continue;
            if (!protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
            if (WebOrControlPorts.Contains(port)) continue;
            // Discovery deliberately retains short-lived sockets, but the
            // endpoint engine must only measure sockets that are active now.
            if (!state.Contains("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;
            candidates.Add(new Candidate(ip, port, protocol, state));
        }

        candidates = FilterCandidates(gameName, candidates);
        if (candidates.Count == 0) return;
        running = true;
        _ = Task.Run(() => RankAndPublish(form, gameName, candidates));
    }

    static List<Candidate> FilterCandidates(string gameName, List<Candidate> candidates)
    {
        if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return candidates;
        var room = candidates.Where(c => !CrossFireMasterPorts.Contains(c.Port)).ToList();
        if (room.Count > 0)
        {
            LogSafe(gameName, room.Count);
            return room;
        }
        // Only fall back to the master/control socket when no other active
        // CrossFire TCP endpoint is visible. This makes the fallback explicit.
        return candidates.Where(c => CrossFireMasterPorts.Contains(c.Port)).ToList();
    }

    static void LogSafe(string gameName, int roomCount) { _ = roomCount; _ = gameName; }

    static async Task RankAndPublish(Form form, string gameName, List<Candidate> candidates)
    {
        try
        {
            var ranked = new List<Result>();
            foreach (var c in candidates.Take(16))
            {
                var samples = await TcpSamples(c.Ip, c.Port, 3).ConfigureAwait(false);
                var good = samples.Where(x => x >= 0).OrderBy(x => x).ToList();
                if (good.Count == 0) continue;
                var median = good[good.Count / 2];
                var average = good.Average();
                var loss = 1.0 - good.Count / 3.0;
                ranked.Add(new Result(c, median, average, loss));
            }
            if (ranked.Count == 0) return;

            var best = ranked.OrderBy(x => x.Median).ThenBy(x => x.Average).First();
            if (ranked.Any(x => $"{x.C.Ip}:{x.C.Port}".Equals(lastTarget, StringComparison.OrdinalIgnoreCase)))
            {
                var current = ranked.First(x => $"{x.C.Ip}:{x.C.Port}".Equals(lastTarget, StringComparison.OrdinalIgnoreCase));
                if (current.Median <= best.Median + 2.0) best = current;
            }
            Publish(form, gameName, best, ranked);
        }
        catch (Exception ex) { Log(form, "[AI] Endpoint engine stopped safely: " + ex.Message); }
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

                bool crossfire = gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase);
                bool masterFallback = crossfire && CrossFireMasterPorts.Contains(best.C.Port);
                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                {
                    metrics.Text = $"ENDPOINT   {best.C.Ip}:{best.C.Port}\r\n" +
                                   $"PROTOCOL   TCP\r\n" +
                                   $"LATENCY    {best.Median:0} ms\r\n" +
                                   $"LOSS       {(best.Loss * 100):0.#}%\r\n" +
                                   $"JITTER     —\r\n" +
                                   $"STABILITY  {Stability(best.Median, best.Loss)}\r\n\r\n" +
                                   (crossfire
                                       ? (masterFallback
                                           ? "* Fallback: no separate room TCP is visible; measuring CrossFire master/control."
                                           : "* Active CrossFire room TCP transport; this is the routing measurement target.")
                                       : "* TCP connect RTT is the transport probe.");
                }

                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                {
                    quality.Text = masterFallback
                        ? $"● FALLBACK MASTER • {best.Median:0} ms • {ranked.Count} CANDIDATE(S)"
                        : $"● ROOM TCP • {best.Median:0} ms • {ranked.Count} CANDIDATE(S)";
                    quality.ForeColor = Color.FromArgb(40, 242, 122);
                }

                var graph = type.GetField("graph", flags)?.GetValue(form);
                graph?.GetType().GetProperty("Values", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(graph, new[] { best.Median });

                var target = $"{best.C.Ip}:{best.C.Port}";
                var changed = !string.Equals(lastTarget, target, StringComparison.OrdinalIgnoreCase) || Math.Abs(lastScore - best.Median) >= 2;
                lastTarget = target; lastScore = best.Median;

                Log(form, $"[ENDPOINT AI] {gameName}: best active TCP = {target} | {best.Median:0} ms | candidates {ranked.Count} | {(masterFallback ? "MASTER FALLBACK" : "ROOM TRANSPORT") }.");
                if (changed)
                    Log(form, $"[ENDPOINT AI] Measurement target updated to {target}. This is an endpoint CrossFire is actively using; no server switch is being forced.");
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
                if (await Task.WhenAny(task, Task.Delay(1000)).ConfigureAwait(false) == task && client.Connected)
                {
                    sw.Stop(); list.Add(sw.Elapsed.TotalMilliseconds);
                }
                else list.Add(-1);
            }
            catch { list.Add(-1); }
            await Task.Delay(60).ConfigureAwait(false);
        }
        return list;
    }

    static bool IsPublic(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || IPAddress.IsLoopback(a)) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
        }
        return !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal;
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

    readonly record struct Candidate(string Ip, int Port, string Protocol, string State);
    readonly record struct Result(Candidate C, double Median, double Average, double Loss);
}
