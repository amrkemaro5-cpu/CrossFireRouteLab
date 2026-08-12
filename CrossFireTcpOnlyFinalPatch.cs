using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Net.Sockets;

namespace CrossFireRouteLab;

/// <summary>
/// Final CrossFire TCP-only decision layer.
/// UDP is deliberately excluded from detection, display, measurement and route scoring.
/// The room/server IP is read from the CrossFire process' live TCP connections.
/// No packets are injected and no router/DNS settings are changed.
/// </summary>
internal static class CrossFireTcpOnlyFinalPatch
{
    static System.Threading.Timer? timer;
    static bool armed;
    static readonly object Sync = new();
    static string lastTarget = "";
    static DateTime lastLog = DateTime.MinValue;

    static readonly HashSet<int> WebPorts = new() { 80, 443, 8080, 8443 };
    static readonly HashSet<int> CrossFireKnownTcpPorts = new() { 10009, 10010, 13008, 16666, 9110 };

    public static void Apply(GameRouteLabV10Form form)
    {
        if (armed || form.IsDisposed) return;
        armed = true;

        // These older layers were the source of UDP candidates. They are left in
        // the project for history/compatibility, but are no longer allowed to run.
        StopTimer("CrossFireRoomTransportPatch");
        StopTimer("CrossFirePacketRoomDiscoveryPatchV2");
        StopTimer("CrossFireRoomTransportProbeV3");

        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 500, 1200);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };

        SetTcpOnlyUi(form);
        Log(form, "[TCP ONLY] CrossFire UDP discovery/measurement is disabled. Room/server IPs are read from live TCP connections only.");
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(GameRouteLabV10Form);
            if (type.GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
            var gameName = type.GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
            if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;

            var found = ReadTcpConnections(form, pid);
            Publish(form, found);
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - lastLog > TimeSpan.FromSeconds(10))
            {
                lastLog = DateTime.UtcNow;
                Log(form, "[TCP ONLY] Discovery warning: " + ex.Message);
            }
        }
    }

    static List<TcpCandidate> ReadTcpConnections(GameRouteLabV10Form form, int pid)
    {
        // The main form already uses netstat -ano -p tcp. Re-read it here so the
        // final TCP-only layer is independent of any UDP-capable legacy patch.
        var text = Run("netstat.exe", "-ano -p tcp", 1800);
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
            if (!IsPublicIPv4(ip) || port <= 0 || WebPorts.Contains(port)) continue;
            result.Add(new TcpCandidate(ip, port, state));
        }

        // Prefer a live non-control TCP connection when one exists. Otherwise the
        // known CrossFire TCP server ports are still shown as a legitimate fallback.
        return result
            .GroupBy(x => $"{x.Ip}:{x.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(x => !IsKnownControlPort(x.Port) && x.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => IsKnownCrossFirePort(x.Port))
            .ThenBy(x => x.Port)
            .Take(24)
            .ToList();
    }

    static void Publish(GameRouteLabV10Form form, List<TcpCandidate> candidates)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        form.BeginInvoke((Action)(async () =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                var active = candidates.Where(x => x.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)).ToList();
                var room = active.Where(x => !IsKnownControlPort(x.Port)).ToList();
                var best = room.FirstOrDefault() ?? active.FirstOrDefault() ?? candidates.FirstOrDefault();
                if (best.Ip.Length == 0) return;

                type.GetField("endpoint", flags)?.SetValue(form, best.Ip);
                type.GetField("endpointPort", flags)?.SetValue(form, best.Port);
                if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box)
                    box.Text = $"{best.Ip}:{best.Port}";

                if (type.GetField("connections", flags)?.GetValue(form) is System.Collections.IList list)
                {
                    list.Clear();
                    var itemType = list.GetType().GetGenericArguments().FirstOrDefault();
                    if (itemType != null)
                    {
                        foreach (var c in candidates)
                        {
                            var label = IsKnownControlPort(c.Port) ? "CROSSFIRE SERVER/CONTROL" : "CROSSFIRE ROOM/SERVER";
                            var item = Activator.CreateInstance(itemType,
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null, new object[] { c.Ip, c.Port, "TCP", $"{c.State} • {label}" }, null);
                            if (item != null) list.Add(item);
                        }
                    }
                }

                if (type.GetField("connectionText", flags)?.GetValue(form) is Label connections)
                {
                    connections.Text = candidates.Count == 0
                        ? "No public CrossFire TCP endpoint visible yet."
                        : string.Join("\r\n", candidates.Take(8).Select(c =>
                            $"TCP  {c.Ip}:{c.Port,-5}  {(IsKnownControlPort(c.Port) ? "SERVER" : "ROOM"),-6}  {c.State}"));
                }

                bool fallback = IsKnownControlPort(best.Port);
                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                {
                    metrics.Text = $"ENDPOINT   {best.Ip}:{best.Port}\r\n" +
                                   "PROTOCOL   TCP\r\n" +
                                   "SOURCE     LIVE CROSSFIRE SOCKET\r\n" +
                                   $"ROLE       {(fallback ? "SERVER / CONTROL FALLBACK" : "ROOM / SERVER TCP")}\r\n" +
                                   $"STATE      {best.State}\r\n" +
                                   "UDP        DISABLED";
                }

                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                {
                    quality.Text = fallback
                        ? $"● CROSSFIRE TCP • SERVER FALLBACK • {best.Ip}:{best.Port}"
                        : $"● CROSSFIRE ROOM TCP • {best.Ip}:{best.Port}";
                    quality.ForeColor = Color.FromArgb(40, 242, 122);
                }

                var target = $"{best.Ip}:{best.Port}";
                if (!target.Equals(lastTarget, StringComparison.OrdinalIgnoreCase))
                {
                    lastTarget = target;
                    Log(form, $"[TCP ONLY] CrossFire TCP target = {target} • {(fallback ? "server/control fallback" : "live room/server candidate")}. UDP is ignored.");
                }

                // Keep the normal TCP endpoint measurement alive as the source of
                // latency. No UDP ping/probe is performed here.
                var samples = await TcpSamples(best.Ip, best.Port, 3).ConfigureAwait(true);
                if (samples.Count > 0 && !form.IsDisposed)
                {
                    var median = samples.OrderBy(x => x).ElementAt(samples.Count / 2);
                    type.GetField("lastPing", flags)?.SetValue(form, median);
                    if (type.GetField("metrics", flags)?.GetValue(form) is Label measured)
                    {
                        measured.Text = $"ENDPOINT   {best.Ip}:{best.Port}\r\n" +
                                        "PROTOCOL   TCP\r\n" +
                                        $"LATENCY    {median:0} ms\r\n" +
                                        $"SAMPLES    {samples.Count}\r\n" +
                                        $"ROLE       {(fallback ? "SERVER / CONTROL FALLBACK" : "ROOM / SERVER TCP")}\r\n" +
                                        "UDP        DISABLED";
                    }
                }
            }
            catch { }
        }));
    }

    static async Task<List<double>> TcpSamples(string ip, int port, int count)
    {
        var values = new List<double>();
        for (var i = 0; i < count; i++)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                var sw = Stopwatch.StartNew();
                var task = client.ConnectAsync(ip, port);
                if (await Task.WhenAny(task, Task.Delay(1200)).ConfigureAwait(true) == task && client.Connected)
                {
                    sw.Stop();
                    values.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            catch { }
            await Task.Delay(60).ConfigureAwait(true);
        }
        return values;
    }

    static bool IsKnownControlPort(int port) => port is 13008 or 16666 or 9110;
    static bool IsKnownCrossFirePort(int port) => CrossFireKnownTcpPorts.Contains(port);

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
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return output.GetAwaiter().GetResult(); }
            return output.GetAwaiter().GetResult() + "\r\n" + error.GetAwaiter().GetResult();
        }
        catch { return ""; }
    }

    static void StopTimer(string typeName)
    {
        try
        {
            var type = typeof(Program).Assembly.GetType("CrossFireRouteLab." + typeName);
            var field = type?.GetField("timer", BindingFlags.Static | BindingFlags.NonPublic);
            if (field?.GetValue(null) is IDisposable d) d.Dispose();
            field?.SetValue(null, null);
        }
        catch { }
    }

    static void SetTcpOnlyUi(GameRouteLabV10Form form)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(GameRouteLabV10Form);
            if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                quality.Text = "● TCP ONLY • WAITING FOR CROSSFIRE ROOM/SERVER";
            if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                metrics.Text = "ENDPOINT   —\r\nPROTOCOL   TCP\r\nROLE       WAITING FOR LIVE CROSSFIRE SOCKET\r\nUDP        DISABLED";
        }
        catch { }
    }

    static void Log(GameRouteLabV10Form form, string text)
    {
        try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }

    readonly record struct TcpCandidate(string Ip, int Port, string State);
}
