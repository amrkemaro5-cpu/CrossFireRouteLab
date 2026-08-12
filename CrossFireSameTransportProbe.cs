using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
        Log(form, "[CROSSFIRE] Room-flow capture enabled: scanning TCP + UDP traffic, not only known 10009 sockets.");
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated || DateTime.UtcNow - last < TimeSpan.FromSeconds(10)) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(GameRouteLabV10Form).GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        var name = typeof(GameRouteLabV10Form).GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (!name.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;
        last = DateTime.UtcNow;
        running = true;
        _ = Task.Run(() => Probe(form));
    }

    static async Task Probe(GameRouteLabV10Form form)
    {
        string? etl = null, pcap = null;
        try
        {
            var known = ReadEndpoints(form);
            var knownKeys = known.Select(x => Key(x.Ip, x.Port)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var root = Path.Combine(Path.GetTempPath(), "GameRouteLab", "CrossFireRoomCapture");
            Directory.CreateDirectory(root);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            etl = Path.Combine(root, $"room-{stamp}.etl");
            pcap = Path.Combine(root, $"room-{stamp}.pcapng");

            await Run("pktmon.exe", "stop").ConfigureAwait(false);
            await Run("pktmon.exe", "filter", "remove").ConfigureAwait(false);

            // Do not filter by the currently-known 10009 port. The room transport may use
            // a dynamically allocated TCP/UDP port that is invisible to the socket table.
            // Two protocol-only filters keep the capture focused without excluding the room.
            await Run("pktmon.exe", "filter", "add", "GRL_CF_TCP", "-t", "TCP").ConfigureAwait(false);
            await Run("pktmon.exe", "filter", "add", "GRL_CF_UDP", "-t", "UDP").ConfigureAwait(false);

            var start = await Run("pktmon.exe", "start", "--capture", "--comp", "nics", "--pkt-size", "256", "--file-name", etl, "--file-size", "32", "--log-mode", "circular").ConfigureAwait(false);
            if (start.Code != 0)
            {
                Log(form, "[CROSSFIRE] Room-flow capture could not start. Run GRL as Administrator.");
                return;
            }

            Log(form, "[CROSSFIRE] Capturing ALL TCP/UDP NIC traffic for 8 seconds. Stay inside the active room.");
            await Task.Delay(8000).ConfigureAwait(false);
            await Run("pktmon.exe", "stop").ConfigureAwait(false);
            await Run("pktmon.exe", "filter", "remove").ConfigureAwait(false);
            if (!File.Exists(etl))
            {
                Log(form, "[CROSSFIRE] Capture file was not produced.");
                return;
            }

            var cv = await Run("pktmon.exe", "etl2pcap", etl, "--out", pcap).ConfigureAwait(false);
            if (cv.Code != 0 || !File.Exists(pcap))
            {
                Log(form, "[CROSSFIRE] Could not convert packet capture to PCAPNG.");
                return;
            }

            var flows = Parse(File.ReadAllBytes(pcap), LocalIPv4s(), knownKeys);
            var candidates = flows
                .Where(x => x.Packets >= 8 && x.Inbound > 0 && x.Outbound > 0 && !IsNoisePort(x.RemotePort))
                .OrderByDescending(x => x.Score)
                .Take(6)
                .ToList();

            if (candidates.Count == 0)
            {
                Log(form, "[CROSSFIRE] No bidirectional public TCP/UDP room-flow candidate was found in this window.");
                return;
            }

            Log(form, $"[CROSSFIRE] Packet scan found {candidates.Count} bidirectional candidate flow(s).");
            foreach (var c in candidates)
                Log(form, $"[CROSSFIRE] CANDIDATE {c.RemoteIp}:{c.RemotePort} {c.Protocol} | {c.Packets} pkts | {c.Outbound} out/{c.Inbound} in | {(c.Known ? "KNOWN" : "NEW")}");

            // Prefer a new, bidirectional flow over the known 10009 master/control flow.
            // Do not claim it is definitely the game server until it survives this correlation test.
            var best = candidates.FirstOrDefault(x => !x.Known);
            if (best.Packets < 8) best = candidates[0];
            form.BeginInvoke((Action)(() => Publish(form, best)));
        }
        catch (Exception ex)
        {
            Log(form, "[CROSSFIRE] Room-flow capture error: " + ex.Message);
        }
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
            metrics.Text = $"ENDPOINT   {f.RemoteIp}:{f.RemotePort}\r\nPROTOCOL   {f.Protocol}\r\nGAME RTT   {rtt}\r\nPACKETS    {f.Packets}\r\nTRAFFIC    {f.Outbound} out / {f.Inbound} in\r\nSTATUS     ROOM-FLOW CANDIDATE • {(f.Known ? "KNOWN" : "NEW FLOW")}";
        }
        if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
        {
            quality.Text = f.Rtt.Count == 0
                ? $"● ROOM FLOW CANDIDATE • {f.Protocol} • {f.Packets} PKTS"
                : $"● GAME TRANSPORT RTT • {Median(f.Rtt):0} ms • {f.Packets} PKTS";
            quality.ForeColor = Color.FromArgb(40, 242, 122);
        }

        Log(form, $"[CROSSFIRE] Selected room-flow candidate: {f.RemoteIp}:{f.RemotePort} {f.Protocol} | {f.Packets} packets | {f.Outbound} out / {f.Inbound} in.");
        if (f.Rtt.Count > 0)
            Log(form, $"[CROSSFIRE] TCP payload/ACK RTT estimate = {Median(f.Rtt):0} ms. This is transport RTT, not ICMP ping.");
        else if (f.Protocol == "UDP")
            Log(form, "[CROSSFIRE] UDP bidirectional flow found. UDP has no built-in ACK, so GRL does not invent an application RTT.");
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

    static List<Flow> Parse(byte[] d, HashSet<string> local, HashSet<string> known)
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
                    var parsed = ParsePacket(d, po, (int)cap, local, known);
                    if (parsed.HasValue)
                    {
                        var p = parsed.Value;
                        var key = Key(p.RemoteIp, p.RemotePort);
                        if (!map.TryGetValue(key, out var mf)) mf = new MutableFlow(p.RemoteIp, p.RemotePort, p.Protocol, p.Known);
                        mf.Packets++;
                        mf.Bytes += p.Bytes;
                        if (p.Out) mf.Outbound++; else mf.Inbound++;
                        map[key] = mf;

                        if (p.Out && p.Protocol == "TCP" && p.Payload > 0)
                            outgoing.Add(new Pending(key, p.SeqEnd, ts));

                        if (!p.Out && p.Protocol == "TCP")
                        {
                            for (int i = outgoing.Count - 1; i >= 0; i--)
                            {
                                if (outgoing[i].Key == key && p.Ack >= outgoing[i].SeqEnd)
                                {
                                    var rtt = (ts - outgoing[i].Time) * 1000.0;
                                    if (rtt > 0 && rtt < 2000) mf.Rtt.Add(rtt);
                                    outgoing.RemoveAt(i);
                                    break;
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

    static Packet? ParsePacket(byte[] d, int o, int len, HashSet<string> local, HashSet<string> known)
    {
        if (len < 34 || BE16(d, o + 12) != 0x0800) return null;
        int ip = o;
        int ihl = (d[ip] & 15) * 4;
        if (ihl < 20 || ip + ihl + 8 > o + len) return null;
        byte proto = d[ip + 9];
        string src = new IPAddress(new ReadOnlySpan<byte>(d, ip + 12, 4)).ToString();
        string dst = new IPAddress(new ReadOnlySpan<byte>(d, ip + 16, 4)).ToString();
        int tr = ip + ihl;
        if (tr + 4 > o + len) return null;
        int sp = BE16(d, tr), dp = BE16(d, tr + 2);
        bool outb = local.Contains(src);
        string rip = outb ? dst : src;
        int rp = outb ? dp : sp;
        if (!IPAddress.TryParse(rip, out var addr) || !IsPublicIPv4(addr)) return null;
        bool isKnown = known.Contains(Key(rip, rp));
        if (proto == 17) return new Packet(rip, rp, "UDP", outb, len, 0, 0, 0, isKnown);
        if (proto != 6 || tr + 20 > o + len) return null;
        uint seq = BE32(d, tr + 4), ack = BE32(d, tr + 8);
        int thl = ((d[tr + 12] >> 4) & 15) * 4;
        if (thl < 20 || tr + thl > o + len) return null;
        int payload = Math.Max(0, len - ihl - thl);
        bool syn = (d[tr + 13] & 2) != 0, fin = (d[tr + 13] & 1) != 0;
        uint seqEnd = seq + (uint)payload + (syn ? 1u : 0u) + (fin ? 1u : 0u);
        return new Packet(rip, rp, "TCP", outb, len, payload, seqEnd, ack, isKnown);
    }

    static bool IsPublicIPv4(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        if (b[0] == 10 || b[0] == 127 || b[0] == 0) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] >= 224) return false;
        return true;
    }

    static bool IsNoisePort(int port) => port is 53 or 80 or 123 or 443 or 853 or 3478 or 5349;

    static string Key(string ip, int port) => $"{ip}:{port}";

    static HashSet<string> LocalIPv4s()
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var a in n.GetIPProperties().UnicastAddresses)
                if (a.Address.AddressFamily == AddressFamily.InterNetwork) s.Add(a.Address.ToString());
        return s;
    }

    static double Median(List<double> x)
    {
        var a = x.OrderBy(v => v).ToList();
        return a[a.Count / 2];
    }

    static uint LE32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
    static uint BE32(byte[] d, int o) => (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);
    static ushort BE16(byte[] d, int o) => (ushort)(d[o] << 8 | d[o + 1]);

    static async Task<RunResult> Run(string file, params string[] args)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
            p.Start();
            await p.WaitForExitAsync().ConfigureAwait(false);
            return new RunResult(p.ExitCode);
        }
        catch { return new RunResult(-1); }
    }

    static void Log(GameRouteLabV10Form form, string text)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text })));
        }
        catch { }
    }

    readonly record struct Endpoint(string Ip, int Port, string Protocol);
    readonly record struct Pending(string Key, uint SeqEnd, double Time);
    readonly record struct Packet(string RemoteIp, int RemotePort, string Protocol, bool Out, int Bytes, int Payload, uint SeqEnd, uint Ack, bool Known);
    readonly record struct RunResult(int Code);

    sealed class MutableFlow
    {
        public string Ip, Protocol;
        public int Port, Packets, Inbound, Outbound;
        public long Bytes;
        public bool Known;
        public List<double> Rtt = new();

        public MutableFlow(string ip, int port, string protocol, bool known)
        {
            Ip = ip;
            Port = port;
            Protocol = protocol;
            Known = known;
        }

        public Flow ToFlow()
        {
            double score = Packets + Math.Min(60, Bytes / 4096.0) + (Inbound > 0 && Outbound > 0 ? 60 : 0) + (Protocol == "UDP" ? 20 : 0);
            if (Known) score -= 500;
            return new Flow(Ip, Port, Protocol, Packets, Inbound, Outbound, Bytes, Rtt, Known, score);
        }
    }

    readonly record struct Flow(string RemoteIp, int RemotePort, string Protocol, int Packets, int Inbound, int Outbound, long Bytes, List<double> Rtt, bool Known, double Score);
}