using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace CrossFireRouteLab;

/// <summary>
/// CrossFire TCP-only live endpoint detector.
/// It reads the actual public TCP sockets owned by the CrossFire process and
/// exposes the best live room/server TCP endpoint to the AI route engine.
/// Latency is measured with fresh TCP connection handshakes to that exact
/// endpoint. This is TCP transport RTT, not ICMP ping and not a fabricated value.
/// </summary>
internal static class CrossFireRoomTransportProbeV3
{
    static System.Threading.Timer? timer;
    static bool running;
    static string targetIp = "";
    static int targetPort;
    static string targetProtocol = "";
    static double tcpRtt = -1;
    static int tcpSamples;
    static string method = "";
    static DateTime lastPublish = DateTime.MinValue;

    static readonly HashSet<int> ControlPorts = new() { 10009, 13008, 16666, 9110 };
    static readonly HashSet<int> NoisePorts = new() { 53, 67, 68, 123, 1900, 3702, 5353, 5222, 3478, 5349, 80, 443, 8080, 8443 };

    public static bool TryGetTarget(out string ip, out int port, out string protocol)
    {
        ip = targetIp;
        port = targetPort;
        protocol = targetProtocol;
        return protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase) && IsPublicIPv4(ip) && port > 0;
    }

    public static bool TryGetPassiveRtt(out double rttMs, out int samples, out string measurementMethod)
    {
        rttMs = tcpRtt;
        samples = tcpSamples;
        measurementMethod = method;
        return rttMs >= 0 && samples > 0;
    }

    public static void Apply(GameRouteLabV10Form form)
    {
        if (form.IsDisposed) return;
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 700, 1800);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[CROSSFIRE TCP] UDP discovery, capture and measurement are disabled. Reading live CrossFire TCP sockets only.");
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(GameRouteLabV10Form).GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        var gameName = typeof(GameRouteLabV10Form).GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;
        running = true;
        _ = Task.Run(() => Scan(form, pid));
    }

    static async Task Scan(GameRouteLabV10Form form, int pid)
    {
        try
        {
            var candidates = ReadTcpConnections(pid);
            var ranked = candidates
                .OrderByDescending(x => x.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) && !ControlPorts.Contains(x.Port))
                .ThenByDescending(x => x.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(x => !ControlPorts.Contains(x.Port))
                .ThenByDescending(x => IsLikelyCrossFireRoomPort(x.Port))
                .ThenBy(x => x.Port)
                .Take(20)
                .ToList();

            if (ranked.Count == 0)
            {
                targetIp = ""; targetPort = 0; targetProtocol = "";
                tcpRtt = -1; tcpSamples = 0; method = "";
                PublishWaiting(form);
                return;
            }

            var measured = new List<(TcpCandidate Candidate, double Ms)>();
            foreach (var candidate in ranked.Where(x => x.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)).Take(8))
            {
                var samples = await TcpConnectSamples(candidate.Ip, candidate.Port, 3).ConfigureAwait(false);
                if (samples.Count > 0)
                    measured.Add((candidate, Median(samples)));
            }

            // Prefer an established non-control socket. If CrossFire exposes only
            // control sockets, use the best live TCP socket as a documented fallback.
            var best = measured
                .OrderBy(x => ControlPorts.Contains(x.Candidate.Port) ? 1 : 0)
                .ThenBy(x => x.Ms)
                .Select(x => x.Candidate)
                .FirstOrDefault();

            if (best.Ip == null)
                best = ranked.First();

            var bestMeasurement = measured.FirstOrDefault(x => x.Candidate.Equals(best));
            targetIp = best.Ip;
            targetPort = best.Port;
            targetProtocol = "TCP";
            tcpRtt = bestMeasurement.Candidate.Equals(default(TcpCandidate)) ? -1 : bestMeasurement.Ms;
            tcpSamples = tcpRtt >= 0 ? 3 : 0;
            method = "TCP connect handshake to live CrossFire socket";

            if (DateTime.UtcNow - lastPublish > TimeSpan.FromSeconds(2))
            {
                lastPublish = DateTime.UtcNow;
                Publish(form, ranked, best, measured);
            }
        }
        catch (Exception ex) { Log(form, "[CROSSFIRE TCP] Detection error: " + ex.Message); }
        finally { running = false; }
    }

    static List<TcpCandidate> ReadTcpConnections(int pid)
    {
        var text = Run("netstat.exe", "-n -o -p tcp", 2500);
        var result = new List<TcpCandidate>();
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith("TCP ", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !int.TryParse(parts[^1], out var ownerPid) || ownerPid != pid) continue;
            var state = parts[^2];
            if (!state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) &&
                !state.Equals("SYN_SENT", StringComparison.OrdinalIgnoreCase) &&
                !state.Equals("SYN_RECEIVED", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryEndpoint(parts[2], out var ip, out var port)) continue;
            if (!IsPublicIPv4(ip) || port <= 0 || NoisePorts.Contains(port)) continue;
            result.Add(new TcpCandidate(ip, port, state));
        }
        return result.GroupBy(x => $"{x.Ip}:{x.Port}", StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
    }

    static async Task<List<double>> TcpConnectSamples(string ip, int port, int count)
    {
        var values = new List<double>();
        for (var i = 0; i < count; i++)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                var sw = Stopwatch.StartNew();
                var connect = client.ConnectAsync(ip, port);
                if (await Task.WhenAny(connect, Task.Delay(1400)).ConfigureAwait(false) == connect && client.Connected)
                {
                    sw.Stop();
                    values.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            catch { }
            await Task.Delay(60).ConfigureAwait(false);
        }
        return values;
    }

    static void Publish(GameRouteLabV10Form form, List<TcpCandidate> candidates, TcpCandidate best, List<(TcpCandidate Candidate, double Ms)> measured)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        form.BeginInvoke((Action)(() =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                type.GetField("endpoint", flags)?.SetValue(form, best.Ip);
                type.GetField("endpointPort", flags)?.SetValue(form, best.Port);
                if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{best.Ip}:{best.Port}";

                if (type.GetField("connections", flags)?.GetValue(form) is System.Collections.IList list)
                {
                    list.Clear();
                    var itemType = list.GetType().GetGenericArguments().FirstOrDefault();
                    if (itemType != null)
                    {
                        foreach (var c in candidates)
                        {
                            var role = ControlPorts.Contains(c.Port) ? "SERVER/CONTROL" : "ROOM/SERVER TCP";
                            var item = Activator.CreateInstance(itemType,
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null, new object[] { c.Ip, c.Port, "TCP", $"{c.State} • {role}" }, null);
                            if (item != null) list.Add(item);
                        }
                    }
                }

                if (type.GetField("connectionText", flags)?.GetValue(form) is Label connectionText)
                    connectionText.Text = string.Join("\r\n", candidates.Take(8).Select(c => $"TCP  {c.Ip}:{c.Port,-5}  {(ControlPorts.Contains(c.Port) ? "SERVER" : "ROOM")}  {c.State}"));

                var measuredBest = measured.FirstOrDefault(x => x.Candidate.Equals(best));
                var hasMeasurement = !measuredBest.Candidate.Equals(default(TcpCandidate));
                var latency = hasMeasurement ? $"{measuredBest.Ms:0} ms" : "n/a";
                var fallback = ControlPorts.Contains(best.Port);
                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                    metrics.Text = $"ENDPOINT   {best.Ip}:{best.Port}\r\n" +
                                   "PROTOCOL   TCP\r\n" +
                                   $"TCP RTT    {latency}\r\n" +
                                   "SOURCE     LIVE CROSSFIRE TCP SOCKET\r\n" +
                                   $"ROLE       {(fallback ? "SERVER / CONTROL FALLBACK" : "ROOM / SERVER TCP")}\r\n" +
                                   "UDP        REMOVED";
                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                {
                    quality.Text = hasMeasurement
                        ? $"● ACTUAL CROSSFIRE TCP • {latency} • {best.Ip}:{best.Port}"
                        : $"● CROSSFIRE TCP • {best.Ip}:{best.Port} • WAITING FOR RTT";
                    quality.ForeColor = Color.FromArgb(40, 242, 122);
                }
                Log(form, $"[CROSSFIRE TCP] Live endpoint = {best.Ip}:{best.Port} • {(fallback ? "control fallback" : "room/server candidate")} • TCP RTT = {latency}.");
            }
            catch { }
        }));
    }

    static void PublishWaiting(GameRouteLabV10Form form)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                if (type.GetField("quality", flags)?.GetValue(form) is Label q) q.Text = "● TCP ONLY • WAITING FOR LIVE CROSSFIRE SOCKET";
                if (type.GetField("metrics", flags)?.GetValue(form) is Label m) m.Text = "ENDPOINT   —\r\nPROTOCOL   TCP\r\nSOURCE     LIVE CROSSFIRE TCP SOCKET\r\nSTATUS     WAITING\r\nUDP        REMOVED";
            }));
        }
        catch { }
    }

    static double Median(List<double> values) => values.OrderBy(x => x).ElementAt(values.Count / 2);
    static bool IsLikelyCrossFireRoomPort(int port) => port is >= 10000 and <= 20000 && !ControlPorts.Contains(port);

    static bool TryEndpoint(string value, out string ip, out int port)
    {
        ip = ""; port = 0; value = value.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var close = value.LastIndexOf(']');
            if (close <= 1 || close + 2 >= value.Length) return false;
            ip = value[1..close];
            return int.TryParse(value[(close + 2)..], out port);
        }
        var colon = value.LastIndexOf(':');
        if (colon <= 0) return false;
        ip = value[..colon];
        return int.TryParse(value[(colon + 1)..], out port);
    }

    static bool IsPublicIPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }

    static string Run(string file, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return output.GetAwaiter().GetResult(); }
            return output.GetAwaiter().GetResult() + "\r\n" + error.GetAwaiter().GetResult();
        }
        catch { return ""; }
    }

    static void Log(GameRouteLabV10Form form, string text)
    {
        try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }

    readonly record struct TcpCandidate(string Ip, int Port, string State);
}
