using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

/// <summary>
/// CrossFire room transport discovery and ranking.
/// The previous v10 logic narrowed CrossFire to TCP/10009, which can be only the
/// publisher/master service. This layer discovers every public TCP/UDP transport
/// exposed by the CrossFire process family and keeps web/CDN sockets out of room ranking.
/// </summary>
internal static class CrossFireRoomTransportPatch
{
    static System.Threading.Timer? timer;
    static bool running;
    static readonly object sync = new();
    static DateTime lastScan = DateTime.MinValue;
    static readonly Dictionary<string, Candidate> seen = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<int> WebPorts = new() { 80, 443, 8080, 8443 };
    static readonly string[] Names = { "crossfire", "crossfire_x64", "crossfire64", "crossfireclient", "crossfireclient64" };

    public static void Apply(GameRouteLabV10Form form)
    {
        if (form.IsDisposed) return;
        StopOldPatch("EndpointMeasurementPatch");
        StopOldPatch("CrossFireConnectionDiscoveryPatch");
        timer = new System.Threading.Timer(_ => Tick(form), null, 900, 1000);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[CROSSFIRE] Final room transport engine enabled: TCP + UDP discovery across the full CrossFire process family.");
    }

    static void StopOldPatch(string typeName)
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

    static void Tick(GameRouteLabV10Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated) return;
        if (DateTime.UtcNow - lastScan < TimeSpan.FromMilliseconds(700)) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(GameRouteLabV10Form).GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        var gameName = typeof(GameRouteLabV10Form).GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;
        lastScan = DateTime.UtcNow;
        running = true;
        _ = Task.Run(() => Scan(form, pid));
    }

    static async Task Scan(GameRouteLabV10Form form, int rootPid)
    {
        try
        {
            var family = DiscoverFamily(rootPid);
            var tcpText = await RunAsync("netstat.exe", "-n -o -p tcp", 2500).ConfigureAwait(false);
            var udpText = await RunAsync("netstat.exe", "-n -o -p udp", 2500).ConfigureAwait(false);
            var tcp = ParseTcp(tcpText, family);
            var udp = ParseUdp(udpText, family);
            var now = DateTime.UtcNow;
            var current = tcp.Concat(udp).Where(IsRoomCandidate).GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

            lock (sync)
            {
                foreach (var c in current) seen[c.Key] = c with { LastSeenUtc = now };
                foreach (var key in seen.Where(x => now - x.Value.LastSeenUtc > TimeSpan.FromSeconds(15)).Select(x => x.Key).ToList()) seen.Remove(key);
            }

            List<Candidate> all;
            lock (sync) all = seen.Values.OrderByDescending(x => x.Protocol == "TCP" && x.State == "ESTABLISHED").ThenBy(x => x.Protocol == "TCP" ? 0 : 1).ThenBy(x => x.Port).Take(40).ToList();
            Publish(form, all);
            await Rank(form, all).ConfigureAwait(false);
        }
        catch (Exception ex) { Log(form, "[CROSSFIRE] Final transport scan error: " + ex.Message); }
        finally { running = false; }
    }

    static bool IsRoomCandidate(Candidate c) => IsPublic(c.Ip) && c.Port > 0 && !WebPorts.Contains(c.Port);

    static HashSet<int> DiscoverFamily(int rootPid)
    {
        var result = new HashSet<int> { rootPid };
        try
        {
            using var root = Process.GetProcessById(rootPid);
            var rootName = root.ProcessName;
            var rootPath = SafePath(root);
            var rootDir = string.IsNullOrWhiteSpace(rootPath) ? "" : Path.GetDirectoryName(rootPath) ?? "";
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    var path = SafePath(p);
                    var sameName = name.Equals(rootName, StringComparison.OrdinalIgnoreCase);
                    var crossfireName = Names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
                    var sameInstall = !string.IsNullOrWhiteSpace(rootDir) && !string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetDirectoryName(path), rootDir, StringComparison.OrdinalIgnoreCase);
                    if (sameName || crossfireName || sameInstall) result.Add(p.Id);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return result;
    }

    static List<Candidate> ParseTcp(string text, HashSet<int> family)
    {
        var result = new List<Candidate>();
        foreach (var line in text.Replace('\r', '\n').Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*TCP\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[4].Value, out var pid) || !family.Contains(pid)) continue;
            var state = m.Groups[3].Value.Trim();
            if (!state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) && !state.Equals("SYN_SENT", StringComparison.OrdinalIgnoreCase) && !state.Equals("SYN_RECEIVED", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryEndpoint(m.Groups[2].Value, out var ip, out var port) || !IsPublic(ip)) continue;
            result.Add(new Candidate(ip, port, "TCP", state, pid, DateTime.UtcNow));
        }
        return result;
    }

    static List<Candidate> ParseUdp(string text, HashSet<int> family)
    {
        var result = new List<Candidate>();
        foreach (var line in text.Replace('\r', '\n').Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*UDP\s+(\S+)\s+(\S+)\s+(\d+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[3].Value, out var pid) || !family.Contains(pid)) continue;
            if (!TryEndpoint(m.Groups[2].Value, out var ip, out var port) || !IsPublic(ip)) continue;
            result.Add(new Candidate(ip, port, "UDP", "CONNECTED", pid, DateTime.UtcNow));
        }
        return result;
    }

    static bool TryEndpoint(string value, out string ip, out int port)
    {
        ip = ""; port = 0; value = value.Trim();
        if (value == "*:*" || value == "0.0.0.0:0" || value == "[::]:0") return false;
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

    static bool IsPublic(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || b[0] >= 224) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
        return true;
    }

    static void Publish(GameRouteLabV10Form form, List<Candidate> candidates)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        form.BeginInvoke((Action)(() =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                var listObj = type.GetField("connections", flags)?.GetValue(form);
                if (listObj is System.Collections.IList list)
                {
                    list.Clear();
                    var itemType = listObj.GetType().GetGenericArguments().FirstOrDefault();
                    if (itemType != null)
                    {
                        foreach (var c in candidates)
                        {
                            var item = Activator.CreateInstance(itemType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { c.Ip, c.Port, c.Protocol, c.State }, null);
                            if (item != null) list.Add(item);
                        }
                    }
                }
                if (type.GetField("connectionText", flags)?.GetValue(form) is Label label)
                    label.Text = candidates.Count == 0 ? "No public CrossFire room transport visible yet." : string.Join("\r\n", candidates.Take(10).Select(c => $"{c.Protocol,-3}  {c.Ip}:{c.Port,-5}  {c.State,-12} PID {c.Pid}"));
                var tcp = candidates.Count(x => x.Protocol == "TCP");
                var udp = candidates.Count(x => x.Protocol == "UDP");
                Log(form, $"[CROSSFIRE] {candidates.Count} public room-transport candidate(s): {tcp} TCP, {udp} UDP.");
                if (tcp > 1) Log(form, "[CROSSFIRE] Multiple TCP room candidates are visible; ranking is no longer locked to TCP/10009.");
                if (udp > 0) Log(form, "[CROSSFIRE] UDP room transport detected. This can explain why the CrossFire scoreboard ping differs from the persistent master TCP socket.");
            }
            catch (Exception ex) { Log(form, "[CROSSFIRE] Publish error: " + ex.Message); }
        }));
    }

    static async Task Rank(GameRouteLabV10Form form, List<Candidate> candidates)
    {
        var tcp = candidates.Where(x => x.Protocol == "TCP" && x.State == "ESTABLISHED").Take(12).ToList();
        var udp = candidates.Where(x => x.Protocol == "UDP").Take(12).ToList();
        var results = new List<(Candidate C, double Ms)>();
        foreach (var c in tcp)
        {
            var samples = new List<double>();
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    using var client = new TcpClient();
                    var sw = Stopwatch.StartNew();
                    await client.ConnectAsync(c.Ip, c.Port).WaitAsync(TimeSpan.FromMilliseconds(1600)).ConfigureAwait(false);
                    sw.Stop(); samples.Add(sw.Elapsed.TotalMilliseconds);
                }
                catch { }
            }
            if (samples.Count > 0) results.Add((c, samples.OrderBy(x => x).ElementAt(samples.Count / 2)));
        }
        foreach (var c in udp)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(c.Ip, 1500).ConfigureAwait(false);
                if (reply.Status == IPStatus.Success) results.Add((c, reply.RoundtripTime));
            }
            catch { }
        }
        if (results.Count == 0) return;
        var best = results.OrderBy(x => x.Ms).First();
        var tcpCount = results.Count(x => x.C.Protocol == "TCP");
        var udpCount = results.Count(x => x.C.Protocol == "UDP");
        form.BeginInvoke((Action)(() =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                    metrics.Text = $"ENDPOINT   {best.C.Ip}:{best.C.Port}\r\nPROTOCOL   {best.C.Protocol}\r\nLATENCY    {best.Ms:0} ms\r\nLOSS       —\r\nJITTER     —\r\nSTABILITY  {Stability(best.Ms)}\r\n\r\n* TCP = connect RTT. UDP = ICMP to exposed room IP; not the scoreboard value.";
                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                    quality.Text = $"● LIVE • {best.Ms:0} ms • {tcpCount + udpCount} MEASURED";
                Log(form, $"[CROSSFIRE] Best exposed room transport = {best.C.Ip}:{best.C.Port} {best.C.Protocol} | {best.Ms:0} ms. TCP measured: {tcpCount}; UDP measured: {udpCount}.");
            }
            catch { }
        }));
    }

    static string Stability(double ms) => ms < 50 ? "EXCELLENT" : ms < 80 ? "HIGH" : ms < 120 ? "GOOD" : "HIGH LATENCY";
    static string SafePath(Process p) { try { return p.MainModule?.FileName ?? ""; } catch { return ""; } }

    static async Task<string> RunAsync(string file, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.ASCII });
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); } catch { try { p.Kill(true); } catch { } }
            return await output.ConfigureAwait(false) + "\r\n" + await error.ConfigureAwait(false);
        }
        catch { return ""; }
    }

    static void Log(GameRouteLabV10Form form, string text)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }

    readonly record struct Candidate(string Ip, int Port, string Protocol, string State, int Pid, DateTime LastSeenUtc)
    {
        public string Key => $"{Protocol}:{Ip}:{Port}";
    }
}
