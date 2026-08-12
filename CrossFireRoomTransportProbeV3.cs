using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;

namespace CrossFireRouteLab;

internal static class CrossFireRoomTransportProbeV3
{
    static System.Threading.Timer? timer;
    static bool running;
    static DateTime lastRun = DateTime.MinValue;
    static string targetIp = "";
    static int targetPort;
    static string targetProtocol = "";
    static double passiveRtt = -1;
    static int passiveSamples;
    static string passiveMethod = "";
    static readonly HashSet<int> ControlPorts = new() { 10009, 13008, 16666 };
    static readonly HashSet<int> NoisePorts = new() { 53, 67, 68, 123, 1900, 3702, 5353, 5222, 3478, 5349, 80, 443, 8080, 8443 };

    public static bool TryGetTarget(out string ip, out int port, out string protocol)
    { ip = targetIp; port = targetPort; protocol = targetProtocol; return IPAddress.TryParse(ip, out _) && port > 0 && protocol.Length > 0; }
    public static bool TryGetPassiveRtt(out double rttMs, out int samples, out string method)
    { rttMs = passiveRtt; samples = passiveSamples; method = passiveMethod; return rttMs >= 0 && samples > 0; }

    public static void Apply(GameRouteLabV10Form form)
    {
        if (form.IsDisposed) return;
        StopTimer("CrossFireRoomTransportPatch"); StopTimer("CrossFirePacketRoomDiscoveryPatchV2"); StopTimer("CrossFireConnectionDiscoveryPatch");
        timer?.Dispose(); timer = new System.Threading.Timer(_ => Tick(form), null, 2500, 3500);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[CROSSFIRE] V3 room capture enabled: passive RTT uses real packet timestamps; no UDP probe is sent.");
    }

    static void StopTimer(string typeName)
    {
        try { var type = typeof(Program).Assembly.GetType("CrossFireRouteLab." + typeName); var field = type?.GetField("timer", BindingFlags.Static | BindingFlags.NonPublic); if (field?.GetValue(null) is IDisposable d) d.Dispose(); field?.SetValue(null, null); } catch { }
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated || DateTime.UtcNow - lastRun < TimeSpan.FromSeconds(10)) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic; var type = typeof(GameRouteLabV10Form);
        if (type.GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        var gameName = type.GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;
        lastRun = DateTime.UtcNow; running = true; _ = Task.Run(() => Capture(form, pid));
    }

    static async Task Capture(GameRouteLabV10Form form, int pid)
    {
        string root = Path.Combine(Path.GetTempPath(), "GameRouteLab", "CrossFireRoomCaptureV3"); Directory.CreateDirectory(root);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff"), etl = Path.Combine(root, $"room-{stamp}.etl"), pcap = Path.Combine(root, $"room-{stamp}.pcapng");
        try
        {
            Log(form, "[CROSSFIRE] Capturing NIC traffic for 12 seconds. Stay inside the active room/match.");
            await RunAsync("pktmon.exe", "filter remove", 3000).ConfigureAwait(false);
            var start = await RunAsync("pktmon.exe", $"start --capture --comp nics --type flow --pkt-size 0 --file-name {Quote(etl)} --file-size 64 --log-mode circular", 5000).ConfigureAwait(false);
            if (start.ExitCode != 0) { Log(form, "[CROSSFIRE] PktMon could not start. Run Game Route Lab as Administrator and retry."); return; }
            await Task.Delay(12000).ConfigureAwait(false); await RunAsync("pktmon.exe", "stop", 5000).ConfigureAwait(false);
            if (!File.Exists(etl)) { Log(form, "[CROSSFIRE] Capture produced no ETL file."); return; }
            var converted = await RunAsync("pktmon.exe", $"etl2pcap {Quote(etl)} --out {Quote(pcap)}", 15000).ConfigureAwait(false);
            if (converted.ExitCode != 0 || !File.Exists(pcap)) { Log(form, "[CROSSFIRE] ETL → PCAPNG conversion failed."); return; }
            var capture = ParsePcapNg(pcap, GetLocalIPv4s());
            var flows = AggregateFlows(capture);
            var candidates = flows.Values.Where(x => x.In > 0 && x.Out > 0 && IsPublic(x.RemoteIp) && !NoisePorts.Contains(x.RemotePort)).OrderByDescending(Score).Take(20).ToList();
            var roomRtt = MeasureTcpRtt(capture, candidates); passiveRtt = roomRtt.RttMs; passiveSamples = roomRtt.Samples; passiveMethod = roomRtt.Method;
            if (candidates.Count == 0) { Log(form, "[CROSSFIRE] Capture proved no bidirectional public room flow in this window."); passiveRtt = -1; passiveSamples = 0; passiveMethod = ""; return; }
            Publish(form, candidates, roomRtt);
        }
        catch (Exception ex) { Log(form, "[CROSSFIRE] V3 error: " + ex.Message); }
        finally { try { await RunAsync("pktmon.exe", "stop", 3000).ConfigureAwait(false); } catch { } running = false; TryDelete(etl); TryDelete(pcap); }
    }

    static List<CapturedPacket> ParsePcapNg(string file, HashSet<string> locals)
    {
        var packets = new List<CapturedPacket>(); using var fs = File.OpenRead(file); using var br = new BinaryReader(fs); double tsUnitSeconds = 1e-6;
        while (fs.Position + 12 <= fs.Length)
        {
            long start = fs.Position; uint type = br.ReadUInt32(), length = br.ReadUInt32(); if (length < 12 || length > fs.Length - start) break; long end = start + length;
            try
            {
                if (type == 0x00000001 && length >= 20)
                {
                    br.ReadUInt16(); br.ReadUInt16(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
                    while (fs.Position + 4 <= end - 4) { ushort code = br.ReadUInt16(), len = br.ReadUInt16(); if (code == 0) break; if (len > end - 4 - fs.Position) break; var data = br.ReadBytes(len); SkipPad(br, len); if (code == 9 && data.Length > 0) { byte v = data[0]; tsUnitSeconds = (v & 0x80) == 0 ? Math.Pow(10, -(v & 0x7f)) : Math.Pow(2, -(v & 0x7f)); } }
                }
                else if ((type == 0x00000006 || type == 0x00000002) && length >= 32)
                {
                    uint hi, lo, capLen;
                    if (type == 0x00000006) { br.ReadUInt32(); hi = br.ReadUInt32(); lo = br.ReadUInt32(); capLen = br.ReadUInt32(); br.ReadUInt32(); }
                    else { br.ReadUInt16(); br.ReadUInt16(); hi = br.ReadUInt32(); lo = br.ReadUInt32(); capLen = br.ReadUInt32(); br.ReadUInt32(); }
                    long packetStart = fs.Position;
                    if (capLen <= end - packetStart - 4) { var data = br.ReadBytes((int)capLen); if (TryParsePacket(data, Timestamp(hi, lo, tsUnitSeconds), locals, out var p)) packets.Add(p); }
                }
            }
            catch { }
            fs.Position = end;
        }
        return packets;
    }

    static DateTime Timestamp(uint hi, uint lo, double unitSeconds)
    { ulong raw = ((ulong)hi << 32) | lo; double ticks = Math.Clamp(raw * unitSeconds * TimeSpan.TicksPerSecond, 0, long.MaxValue); return DateTime.UnixEpoch.AddTicks((long)ticks); }
    static void SkipPad(BinaryReader br, int len) { int pad = (4 - (len & 3)) & 3; if (pad > 0) br.ReadBytes(pad); }

    static bool TryParsePacket(byte[] packet, DateTime ts, HashSet<string> locals, out CapturedPacket result)
    {
        result = default; if (packet.Length < 14) return false; int ether = U16(packet, 12), offset = 14;
        if (ether is 0x8100 or 0x88A8 or 0x9100) { if (packet.Length < 18) return false; ether = U16(packet, 16); offset = 18; }
        if (ether != 0x0800 || packet.Length < offset + 20) return false; int ihl = (packet[offset] & 0x0f) * 4; if (ihl < 20 || packet.Length < offset + ihl) return false;
        int protocol = packet[offset + 9]; string src = new IPAddress(packet.AsSpan(offset + 12, 4)).ToString(), dst = new IPAddress(packet.AsSpan(offset + 16, 4)).ToString(); bool srcLocal = locals.Contains(src), dstLocal = locals.Contains(dst); if (srcLocal == dstLocal) return false;
        int totalLen = U16(packet, offset + 2), ipEnd = Math.Min(packet.Length, offset + Math.Max(ihl, totalLen)), transport = offset + ihl; if (protocol is not 6 and not 17 || packet.Length < transport + 8) return false;
        int srcPort = U16(packet, transport), dstPort = U16(packet, transport + 2); string remote = srcLocal ? dst : src; int remotePort = srcLocal ? dstPort : srcPort; if (!IsPublic(remote) || remotePort <= 0) return false;
        string proto = protocol == 17 ? "UDP" : "TCP"; int payloadOffset = transport + 8; uint seq = 0, ack = 0, tsval = 0, tsecr = 0; byte flags = 0; bool hasTs = false;
        if (protocol == 6)
        {
            if (packet.Length < transport + 20) return false; seq = U32(packet, transport + 4); ack = U32(packet, transport + 8); flags = packet[transport + 13]; int tcpLen = ((packet[transport + 12] >> 4) & 0x0f) * 4; if (tcpLen < 20 || packet.Length < transport + tcpLen) return false; payloadOffset = transport + tcpLen;
            for (int p = transport + 20, end = transport + tcpLen; p + 1 < end; ) { byte kind = packet[p]; if (kind == 0) break; if (kind == 1) { p++; continue; } int len = packet[p + 1]; if (len < 2 || p + len > end) break; if (kind == 8 && len == 10) { tsval = U32(packet, p + 2); tsecr = U32(packet, p + 6); hasTs = true; } p += len; }
        }
        int payloadLen = Math.Max(0, ipEnd - payloadOffset); result = new CapturedPacket(ts, remote, remotePort, proto, srcLocal, seq, ack, flags, payloadLen, tsval, tsecr, hasTs); return true;
    }

    static Dictionary<string, Flow> AggregateFlows(List<CapturedPacket> packets)
    {
        var result = new Dictionary<string, Flow>(StringComparer.OrdinalIgnoreCase); foreach (var p in packets) { string key = $"{p.Protocol}|{p.RemoteIp}:{p.RemotePort}"; if (!result.TryGetValue(key, out var f)) f = new Flow(p.RemoteIp, p.RemotePort, p.Protocol, 0, 0); result[key] = p.Outbound ? f with { Out = f.Out + 1 } : f with { In = f.In + 1 }; } return result;
    }

    static (double RttMs, int Samples, string Method) MeasureTcpRtt(List<CapturedPacket> packets, List<Flow> candidates)
    {
        var allowed = candidates.Where(x => x.Protocol == "TCP" && !ControlPorts.Contains(x.RemotePort)).Select(x => $"{x.RemoteIp}:{x.RemotePort}").ToHashSet(StringComparer.OrdinalIgnoreCase); if (allowed.Count == 0) return (-1, 0, "");
        var seqOutstanding = new Dictionary<string, Queue<(uint End, DateTime Sent)>>(); var tsOutstanding = new Dictionary<string, Dictionary<uint, DateTime>>(); var samples = new List<double>();
        foreach (var p in packets.OrderBy(x => x.Timestamp))
        {
            string key = $"{p.RemoteIp}:{p.RemotePort}"; if (!allowed.Contains(key)) continue;
            if (p.Outbound)
            {
                if (p.PayloadLength > 0 || (p.Flags & 0x03) != 0) { if (!seqOutstanding.TryGetValue(key, out var q)) seqOutstanding[key] = q = new Queue<(uint, DateTime)>(); uint end = p.Sequence + (uint)p.PayloadLength + (((p.Flags & 0x02) != 0) ? 1u : 0u) + (((p.Flags & 0x01) != 0) ? 1u : 0u); q.Enqueue((end, p.Timestamp)); }
                if (p.HasTcpTimestamp && p.TimestampValue != 0) { if (!tsOutstanding.TryGetValue(key, out var map)) tsOutstanding[key] = map = new Dictionary<uint, DateTime>(); map[p.TimestampValue] = p.Timestamp; }
            }
            else
            {
                if ((p.Flags & 0x10) != 0 && seqOutstanding.TryGetValue(key, out var q)) while (q.Count > 0) { var item = q.Peek(); if (!SequenceLE(item.End, p.Ack)) break; q.Dequeue(); var ms = (p.Timestamp - item.Sent).TotalMilliseconds; if (ms is >= 0 and <= 5000) samples.Add(ms); }
                if (p.HasTcpTimestamp && p.TimestampEcho != 0 && tsOutstanding.TryGetValue(key, out var map) && map.Remove(p.TimestampEcho, out var sent)) { var ms = (p.Timestamp - sent).TotalMilliseconds; if (ms is >= 0 and <= 5000) samples.Add(ms); }
            }
        }
        if (samples.Count == 0) return (-1, 0, ""); samples.Sort(); return (samples[samples.Count / 2], samples.Count, "TCP ACK/timestamp correlation");
    }

    static bool SequenceLE(uint a, uint b) => unchecked((int)(a - b)) <= 0;
    static int U16(byte[] b, int o) => (b[o] << 8) | b[o + 1];
    static uint U32(byte[] b, int o) => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];
    static double Score(Flow f) { double score = f.In + f.Out; if (f.In > 0 && f.Out > 0) score += 100; if (f.Protocol == "UDP") score += 35; if (f.Protocol == "TCP") score += 20; if (f.RemotePort is >= 12000 and <= 14000) score += 100; else if (f.RemotePort is >= 11000 and <= 16000) score += 55; if (ControlPorts.Contains(f.RemotePort)) score -= 160; return score; }

    static void Publish(GameRouteLabV10Form form, List<Flow> candidates, (double RttMs, int Samples, string Method) roomRtt)
    {
        if (form.IsDisposed || !form.IsHandleCreated || candidates.Count == 0) return;
        form.BeginInvoke((Action)(() =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic; var type = typeof(GameRouteLabV10Form); var room = candidates.Where(x => !ControlPorts.Contains(x.RemotePort)).ToList(); var tcpRoom = room.FirstOrDefault(x => x.Protocol == "TCP"); var best = tcpRoom.RemoteIp != null ? tcpRoom : room.FirstOrDefault(); if (best.RemoteIp == null) best = candidates[0]; bool actual = !ControlPorts.Contains(best.RemotePort);
                targetIp = best.RemoteIp; targetPort = best.RemotePort; targetProtocol = best.Protocol;
                if (type.GetField("connectionText", flags)?.GetValue(form) is Label label) label.Text = string.Join("\r\n", candidates.Take(10).Select(c => $"{c.Protocol,-3}  {c.RemoteIp}:{c.RemotePort,-5}  {(ControlPorts.Contains(c.RemotePort) ? "CONTROL" : "ROOM FLOW"),-10}  {c.In} IN / {c.Out} OUT"));
                type.GetField("endpoint", flags)?.SetValue(form, best.RemoteIp); type.GetField("endpointPort", flags)?.SetValue(form, best.RemotePort); if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{best.RemoteIp}:{best.RemotePort}";
                if (type.GetField("metrics", flags)?.GetValue(form) is Label m) { string latency = roomRtt.RttMs >= 0 && best.Protocol == "TCP" ? $"PASSIVE RTT {roomRtt.RttMs:0} ms" : "PASSIVE RTT — (no safe ACK correlation)"; m.Text = $"ENDPOINT   {best.RemoteIp}:{best.RemotePort}\r\nPROTOCOL   {best.Protocol}\r\nTRAFFIC    {best.In + best.Out} packets\r\nDIRECTION  {best.Out} out / {best.In} in\r\n{latency}\r\nSTATUS     {(actual ? "ACTUAL ROOM FLOW" : "CONTROL FLOW ONLY")}"; m.ForeColor = Green; }
                if (type.GetField("quality", flags)?.GetValue(form) is Label q) { q.Text = actual ? (roomRtt.RttMs >= 0 ? $"● ACTUAL ROOM • {roomRtt.RttMs:0} ms PASSIVE RTT" : $"● ACTUAL ROOM • {best.Protocol} • PASSIVE RTT PENDING") : "● CONTROL ONLY"; q.ForeColor = actual ? Green : Muted; }
                Log(form, $"[CROSSFIRE] V3 found {candidates.Count} bidirectional public flow(s)."); Log(form, $"[CROSSFIRE] V3 selected {best.RemoteIp}:{best.RemotePort} {best.Protocol} • {best.Out} out / {best.In} in.");
                if (actual && roomRtt.RttMs >= 0) Log(form, $"[CROSSFIRE] PASSIVE ROOM RTT = {roomRtt.RttMs:0.0} ms from {roomRtt.Samples} {roomRtt.Method} sample(s). No synthetic game packets were sent."); else if (actual) Log(form, "[CROSSFIRE] Room traffic is confirmed, but this capture had no safe passive RTT correlation. UDP probe results are intentionally not used as game latency."); else Log(form, "[CROSSFIRE] Only control transports were observed; no separate room flow was proven in this capture.");
            }
            catch (Exception ex) { Log(form, "[CROSSFIRE] V3 publish error: " + ex.Message); }
        }));
    }

    static HashSet<string> GetLocalIPv4s() { var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase); try { foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up)) foreach (var a in n.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)) set.Add(a.Address.ToString()); } catch { } return set; }
    static bool IsPublic(string value) { if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false; var b = ip.GetAddressBytes(); return !(b[0] == 10 || b[0] == 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)); }
    static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";
    static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, int timeoutMs) { try { using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.ASCII }); if (p == null) return (-1, ""); var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync(); using var cts = new CancellationTokenSource(timeoutMs); try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); } catch { try { p.Kill(true); } catch { } } return (p.ExitCode, await output.ConfigureAwait(false) + "\r\n" + await error.ConfigureAwait(false)); } catch (Exception ex) { return (-1, ex.Message); } }
    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    static void Log(GameRouteLabV10Form form, string text) { if (form.IsDisposed || !form.IsHandleCreated) return; try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { } }
    readonly record struct CapturedPacket(DateTime Timestamp, string RemoteIp, int RemotePort, string Protocol, bool Outbound, uint Sequence, uint Ack, byte Flags, int PayloadLength, uint TimestampValue, uint TimestampEcho, bool HasTcpTimestamp);
    readonly record struct Flow(string RemoteIp, int RemotePort, string Protocol, int In, int Out);
}
