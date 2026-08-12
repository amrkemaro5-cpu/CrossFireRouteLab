using System.Diagnostics;
using System.Net;
using System.Reflection;

namespace CrossFireRouteLab;

internal static class CrossFireSameTransportProbe
{
    static System.Threading.Timer? timer;
    static bool running;
    static DateTime last = DateTime.MinValue;

    public static void Apply(GameRouteLabV10Form form)
    {
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 5000, 5000);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[CROSSFIRE] Same-transport probe enabled: room traffic can be multiplexed on the channel/master socket.");
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated || DateTime.UtcNow - last < TimeSpan.FromSeconds(8)) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(GameRouteLabV10Form).GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        var name = typeof(GameRouteLabV10Form).GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (!name.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;
        last = DateTime.UtcNow; running = true;
        _ = Task.Run(() => Probe(form));
    }

    static async Task Probe(GameRouteLabV10Form form)
    {
        string? etl = null, pcap = null;
        try
        {
            var endpoints = ReadEndpoints(form);
            if (endpoints.Count == 0) return;
            var ports = endpoints.Select(x => x.Port).Where(x => x > 0).Distinct().Take(8).ToList();
            var root = Path.Combine(Path.GetTempPath(), "GameRouteLab", "CrossFireSameTransport");
            Directory.CreateDirectory(root);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            etl = Path.Combine(root, $"same-{stamp}.etl"); pcap = Path.Combine(root, $"same-{stamp}.pcapng");

            await Run("pktmon.exe", "filter", "remove").ConfigureAwait(false);
            foreach (var port in ports)
            {
                await Run("pktmon.exe", "filter", "add", $"GRL_CF_{port}_TCP", "-p", port.ToString(), "-t", "TCP").ConfigureAwait(false);
                await Run("pktmon.exe", "filter", "add", $"GRL_CF_{port}_UDP", "-p", port.ToString(), "-t", "UDP").ConfigureAwait(false);
            }
            var start = await Run("pktmon.exe", "start", "--capture", "--comp", "nics", "--pkt-size", "256", "--file-name", etl, "--file-size", "16", "--log-mode", "circular").ConfigureAwait(false);
            if (start.Code != 0) { Log(form, "[CROSSFIRE] Same-transport packet probe could not start; run GRL as Administrator."); return; }
            await Task.Delay(5000).ConfigureAwait(false);
            await Run("pktmon.exe", "stop").ConfigureAwait(false);
            await Run("pktmon.exe", "filter", "remove").ConfigureAwait(false);
            if (!File.Exists(etl)) return;
            var cv = await Run("pktmon.exe", "etl2pcap", etl, "--out", pcap).ConfigureAwait(false);
            if (cv.Code != 0 || !File.Exists(pcap)) return;

            var flows = Parse(File.ReadAllBytes(pcap), LocalIPv4s(), endpoints);
            var best = flows.Where(x => x.Packets >= 3).OrderByDescending(x => x.Score).FirstOrDefault();
            if (best.Packets < 3) { Log(form, "[CROSSFIRE] No room packets were correlated during this probe window."); return; }
            form.BeginInvoke((Action)(() => Publish(form, best)));
        }
        catch (Exception ex) { Log(form, "[CROSSFIRE] Same-transport probe error: " + ex.Message); }
        finally
        {
            try { await Run("pktmon.exe", "stop").ConfigureAwait(false); } catch { }
            try { await Run("pktmon.exe", "filter", "remove").ConfigureAwait(false); } catch { }
            try { if (etl != null && File.Exists(etl)) File.Delete(etl); } catch { }
            try { if (pcap != null && File.Exists(pcap)) File.Delete(pcap); } catch { }
            running = false;
        }
    }

    static void Publish(GameRouteLabV10Form form, Flow f)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(GameRouteLabV10Form);
        type.GetField("endpoint", flags)?.SetValue(form, f.RemoteIp);
        type.GetField("endpointPort", flags)?.SetValue(form, f.RemotePort);
        if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{f.RemoteIp}:{f.RemotePort}";
        if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
        {
            var rtt = f.Rtt.Count == 0 ? "n/a" : $"{Median(f.Rtt):0} ms";
            metrics.Text = $"ENDPOINT   {f.RemoteIp}:{f.RemotePort}\r\nPROTOCOL   {f.Protocol}\r\nGAME RTT   {rtt}\r\nPACKETS    {f.Packets}\r\nTRAFFIC    {f.Outbound} out / {f.Inbound} in\r\nSTATUS     SAME TRANSPORT • ROOM TRAFFIC";
        }
        if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
        {
            quality.Text = f.Rtt.Count == 0 ? $"● ROOM TRAFFIC FOUND • {f.Protocol} • {f.Packets} PKTS" : $"● GAME TRANSPORT RTT • {Median(f.Rtt):0} ms • {f.Packets} PKTS";
            quality.ForeColor = Color.FromArgb(40, 242, 122);
        }
        Log(form, $"[CROSSFIRE] Room traffic is using the same transport: {f.RemoteIp}:{f.RemotePort} {f.Protocol} | {f.Packets} packets | {f.Outbound} out / {f.Inbound} in.");
        if (f.Rtt.Count > 0) Log(form, $"[CROSSFIRE] TCP packet RTT estimate = {Median(f.Rtt):0} ms from live ACK timing. This is transport RTT, not an ICMP ping.");
        else Log(form, "[CROSSFIRE] UDP traffic is present on the same endpoint. UDP has no built-in ACK, so GRL does not invent an application RTT.");
    }

    static List<Endpoint> ReadEndpoints(GameRouteLabV10Form form)
    {
        var result = new List<Endpoint>();
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            if (typeof(GameRouteLabV10Form).GetField("connections", flags)?.GetValue(form) is not System.Collections.IEnumerable list) return result;
            foreach (var item in list)
            {
                if (item == null) continue;
                var t = item.GetType();
                var ip = t.GetProperty("Ip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item)?.ToString() ?? "";
                var port = Convert.ToInt32(t.GetProperty("Port", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item) ?? 0);
                var protocol = t.GetProperty("Protocol", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item)?.ToString() ?? "TCP";
                if (IPAddress.TryParse(ip, out _) && port > 0) result.Add(new Endpoint(ip, port, protocol));
            }
        }
        catch { }
        return result;
    }

    static List<Flow> Parse(byte[] d, HashSet<string> local, List<Endpoint> known)
    {
        var map = new Dictionary<string, MutableFlow>(StringComparer.OrdinalIgnoreCase);
        var outgoing = new List<Pending>();
        int o = 0;
        while (o + 12 <= d.Length)
        {
            uint type = LE32(d, o), len = LE32(d, o + 4);
            if (len < 12 || o + len > d.Length) break;
            if (type == 0x00000006 && len >= 32)
            {
                uint hi = BE32(d, o + 12), lo = BE32(d, o + 16), cap = LE32(d, o + 20);
                double ts = ((ulong)hi << 32 | lo) / 1_000_000.0;
                int po = o + 28;
                if (cap > 0 && po + cap <= o + len - 4)
                {
                    var p = ParsePacket(d, po, (int)cap, local, known);
                    if (p != null)
                    {
                        var key = $"{p.RemoteIp}:{p.RemotePort}";
                        if (!map.TryGetValue(key, out var mf)) mf = new MutableFlow(p.RemoteIp, p.RemotePort, p.Protocol);
                        mf.Packets++; mf.Bytes += p.Bytes; if (p.Out) mf.Outbound++; else mf.Inbound++;
                        map[key] = mf;
                        if (p.Out && p.Protocol == "TCP" && p.Payload > 0) outgoing.Add(new Pending(key, p.SeqEnd, ts));
                        if (!p.Out && p.Protocol == "TCP")
                        {
                            for (int i = outgoing.Count - 1; i >= 0; i--)
                            {
                                if (outgoing[i].Key == key && p.Ack >= outgoing[i].SeqEnd)
                                {
                                    var rtt = (ts - outgoing[i].Time) * 1000.0;
                                    if (rtt > 0 && rtt < 2000) mf.Rtt.Add(rtt);
                                    outgoing.RemoveAt(i); break;
                                }
                            }
                        }
                    }
                }
            }
            o += (int)len;
        }
        return map.Values.Select(x => x.ToFlow()).ToList();
    }

    static Packet? ParsePacket(byte[] d, int o, int len, HashSet<string> local, List<Endpoint> known)
    {
        if (len < 34 || BE16(d, o + 12) != 0x0800) return null;
        int ip = o, ihl = (d[ip] & 15) * 4; if (ihl < 20 || ip + ihl + 8 > o + len) return null;
        byte proto = d[ip + 9];
        string src = new IPAddress(new ReadOnlySpan<byte>(d, ip + 12, 4)).ToString();
        string dst = new IPAddress(new ReadOnlySpan<byte>(d, ip + 16, 4)).ToString();
        int tr = ip + ihl, sp = BE16(d, tr), dp = BE16(d, tr + 2); bool outb = local.Contains(src); string rip = outb ? dst : src; int rp = outb ? dp : sp;
        if (!known.Any(x => x.Ip.Equals(rip, StringComparison.OrdinalIgnoreCase) && x.Port == rp)) return null;
        if (proto == 17) return new Packet(rip, rp, "UDP", outb, len, 0, 0, 0);
        if (proto != 6 || tr + 20 > o + len) return null;
        uint seq = BE32(d, tr + 4), ack = BE32(d, tr + 8); int thl = ((d[tr + 12] >> 4) & 15) * 4; if (thl < 20 || tr + thl > o + len) return null;
        int payload = Math.Max(0, len - ihl - thl);
        bool syn = (d[tr + 13] & 2) != 0, fin = (d[tr + 13] & 1) != 0;
        uint seqEnd = seq + (uint)payload + (syn ? 1u : 0u) + (fin ? 1u : 0u);
        return new Packet(rip, rp, "TCP", outb, len, payload, seqEnd, ack);
    }

    static HashSet<string> LocalIPv4s()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()) foreach (var a in n.GetIPProperties().UnicastAddresses) if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) s.Add(a.Address.ToString());
        return s;
    }
    static double Median(List<double> x) { var a = x.OrderBy(v => v).ToList(); return a[a.Count / 2]; }
    static uint LE32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
    static uint BE32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    static ushort BE16(byte[] d, int o) => (ushort)(d[o] << 8 | d[o + 1]);
    static async Task<RunResult> Run(string file, params string[] args)
    {
        try { using var p = new Process(); p.StartInfo = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var a in args) p.StartInfo.ArgumentList.Add(a); p.Start(); await p.WaitForExitAsync().ConfigureAwait(false); return new RunResult(p.ExitCode); } catch { return new RunResult(-1); }
    }
    static void Log(GameRouteLabV10Form form, string text) { if (form.IsDisposed || !form.IsHandleCreated) return; try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { } }

    readonly record struct Endpoint(string Ip, int Port, string Protocol);
    readonly record struct Pending(string Key, uint SeqEnd, double Time);
    readonly record struct Packet(string RemoteIp, int RemotePort, string Protocol, bool Out, int Bytes, int Payload, uint SeqEnd, uint Ack);
    readonly record struct RunResult(int Code);
    sealed class MutableFlow
    {
        public string Ip, Protocol; public int Port, Packets, Inbound, Outbound; public long Bytes; public List<double> Rtt = new();
        public MutableFlow(string ip, int port, string protocol) { Ip = ip; Port = port; Protocol = protocol; }
        public Flow ToFlow() => new(Ip, Port, Protocol, Packets, Inbound, Outbound, Bytes, Rtt, Packets + Rtt.Count * 20);
    }
    readonly record struct Flow(string RemoteIp, int RemotePort, string Protocol, int Packets, int Inbound, int Outbound, long Bytes, List<double> Rtt, double Score);
}
