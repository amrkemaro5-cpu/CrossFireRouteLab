using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;

namespace CrossFireRouteLab;

/// <summary>
/// Authoritative CrossFire room-flow probe.
/// Uses Windows PktMon for capture and parses the resulting PCAPNG directly,
/// rather than depending on PktMon's human-readable text formatting.
/// </summary>
internal static class CrossFireRoomTransportProbeV3
{
    static System.Threading.Timer? timer;
    static bool running;
    static DateTime lastRun = DateTime.MinValue;
    static string targetIp = "";
    static int targetPort;
    static string targetProtocol = "";

    static readonly HashSet<int> ControlPorts = new() { 10009, 13008, 16666 };
    static readonly HashSet<int> NoisePorts = new() { 53, 67, 68, 123, 1900, 3702, 5353, 5222, 3478, 5349, 80, 443, 8080, 8443 };

    public static bool TryGetTarget(out string ip, out int port, out string protocol)
    {
        ip = targetIp;
        port = targetPort;
        protocol = targetProtocol;
        return IPAddress.TryParse(ip, out _) && port > 0 && protocol.Length > 0;
    }

    public static void Apply(GameRouteLabV10Form form)
    {
        if (form.IsDisposed) return;

        StopTimer("CrossFireRoomTransportPatch");
        StopTimer("CrossFirePacketRoomDiscoveryPatchV2");
        StopTimer("CrossFireConnectionDiscoveryPatch");

        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 2500, 3500);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[CROSSFIRE] V3 authoritative room probe enabled. PktMon PCAPNG is now the source of truth.");
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

    static void Tick(GameRouteLabV10Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated) return;
        if (DateTime.UtcNow - lastRun < TimeSpan.FromSeconds(10)) return;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(GameRouteLabV10Form);
        if (type.GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        var gameName = type.GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;

        lastRun = DateTime.UtcNow;
        running = true;
        _ = Task.Run(() => Capture(form, pid));
    }

    static async Task Capture(GameRouteLabV10Form form, int pid)
    {
        string root = Path.Combine(Path.GetTempPath(), "GameRouteLab", "CrossFireRoomCaptureV3");
        Directory.CreateDirectory(root);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        string etl = Path.Combine(root, $"room-{stamp}.etl");
        string pcap = Path.Combine(root, $"room-{stamp}.pcapng");

        try
        {
            Log(form, "[CROSSFIRE] V3 capturing network packets for 12 seconds. Stay inside the active room/match.");

            await RunAsync("pktmon.exe", "filter remove", 3000).ConfigureAwait(false);
            var start = await RunAsync("pktmon.exe", $"start --capture --comp nics --type flow --pkt-size 0 --file-name {Quote(etl)} --file-size 64 --log-mode circular", 5000).ConfigureAwait(false);
            if (start.ExitCode != 0)
            {
                Log(form, "[CROSSFIRE] V3 could not start PktMon. Start Game Route Lab as Administrator and retry.");
                return;
            }

            await Task.Delay(12000).ConfigureAwait(false);
            await RunAsync("pktmon.exe", "stop", 5000).ConfigureAwait(false);
            if (!File.Exists(etl))
            {
                Log(form, "[CROSSFIRE] V3 capture produced no ETL file.");
                return;
            }

            var converted = await RunAsync("pktmon.exe", $"etl2pcap {Quote(etl)} --out {Quote(pcap)}", 15000).ConfigureAwait(false);
            if (converted.ExitCode != 0 || !File.Exists(pcap))
            {
                Log(form, "[CROSSFIRE] V3 could not convert the capture to PCAPNG.");
                return;
            }

            var flows = ParsePcapNg(pcap, GetLocalIPv4s());
            var candidates = flows.Values
                .Where(x => x.In > 0 && x.Out > 0 && IsPublic(x.RemoteIp) && !NoisePorts.Contains(x.RemotePort))
                .OrderByDescending(Score)
                .Take(20)
                .ToList();

            if (candidates.Count == 0)
            {
                Log(form, "[CROSSFIRE] V3 captured packets but proved no bidirectional public room flow in this window.");
                Log(form, "[CROSSFIRE] This is an observation result, not a guessed endpoint; keep the match active for the next capture.");
                return;
            }

            Publish(form, candidates);
        }
        catch (Exception ex)
        {
            Log(form, "[CROSSFIRE] V3 probe error: " + ex.Message);
        }
        finally
        {
            try { await RunAsync("pktmon.exe", "stop", 3000).ConfigureAwait(false); } catch { }
            running = false;
            TryDelete(etl);
            TryDelete(pcap);
        }
    }

    static Dictionary<string, Flow> ParsePcapNg(string file, HashSet<string> locals)
    {
        var result = new Dictionary<string, Flow>(StringComparer.OrdinalIgnoreCase);
        using var fs = File.OpenRead(file);
        using var br = new BinaryReader(fs);

        while (fs.Position + 12 <= fs.Length)
        {
            long blockStart = fs.Position;
            uint blockType = br.ReadUInt32();
            uint blockLength = br.ReadUInt32();
            if (blockLength < 12 || blockLength > fs.Length - blockStart) break;

            long blockEnd = blockStart + blockLength;
            try
            {
                if (blockType == 0x00000006) // Enhanced Packet Block
                {
                    if (blockLength >= 32)
                    {
                        br.ReadUInt32(); // interface id
                        br.ReadUInt32(); // timestamp high
                        br.ReadUInt32(); // timestamp low
                        uint capturedLength = br.ReadUInt32();
                        br.ReadUInt32(); // original length

                        long packetStart = fs.Position;
                        if (capturedLength <= blockEnd - packetStart - 4)
                        {
                            var packet = br.ReadBytes((int)capturedLength);
                            ParseEthernetPacket(packet, locals, result);
                        }
                    }
                }
                else if (blockType == 0x00000002) // Packet Block
                {
                    if (blockLength >= 32)
                    {
                        br.ReadUInt16(); // interface id
                        br.ReadUInt16(); // drops count
                        br.ReadUInt32(); // timestamp high
                        br.ReadUInt32(); // timestamp low
                        uint capturedLength = br.ReadUInt32();
                        br.ReadUInt32(); // original length
                        long packetStart = fs.Position;
                        if (capturedLength <= blockEnd - packetStart - 4)
                        {
                            var packet = br.ReadBytes((int)capturedLength);
                            ParseEthernetPacket(packet, locals, result);
                        }
                    }
                }
            }
            catch { }

            fs.Position = blockEnd;
        }

        return result;
    }

    static void ParseEthernetPacket(byte[] packet, HashSet<string> locals, Dictionary<string, Flow> result)
    {
        if (packet.Length < 14) return;
        int etherType = ReadU16(packet, 12);
        int offset = 14;

        // VLAN / QinQ
        if (etherType == 0x8100 || etherType == 0x88A8 || etherType == 0x9100)
        {
            if (packet.Length < 18) return;
            etherType = ReadU16(packet, 16);
            offset = 18;
        }
        if (etherType != 0x0800 || packet.Length < offset + 20) return; // IPv4 only for now

        int ihl = (packet[offset] & 0x0F) * 4;
        if (ihl < 20 || packet.Length < offset + ihl) return;
        int protocol = packet[offset + 9];
        string src = new IPAddress(packet.AsSpan(offset + 12, 4)).ToString();
        string dst = new IPAddress(packet.AsSpan(offset + 16, 4)).ToString();
        if (!locals.Contains(src) && !locals.Contains(dst)) return;
        bool srcLocal = locals.Contains(src);
        if (srcLocal == locals.Contains(dst)) return;

        int transport = offset + ihl;
        if (protocol != 6 && protocol != 17) return;
        if (packet.Length < transport + 4) return;

        int srcPort = ReadU16(packet, transport);
        int dstPort = ReadU16(packet, transport + 2);
        string remote = srcLocal ? dst : src;
        int remotePort = srcLocal ? dstPort : srcPort;
        if (!IsPublic(remote) || remotePort <= 0) return;

        string proto = protocol == 17 ? "UDP" : "TCP";
        string key = $"{proto}|{remote}:{remotePort}";
        if (!result.TryGetValue(key, out var flow))
            flow = new Flow(remote, remotePort, proto, 0, 0);

        result[key] = srcLocal
            ? flow with { Out = flow.Out + 1 }
            : flow with { In = flow.In + 1 };
    }

    static int ReadU16(byte[] b, int offset) => (b[offset] << 8) | b[offset + 1];

    static double Score(Flow f)
    {
        double score = f.In + f.Out;
        if (f.In > 0 && f.Out > 0) score += 100;
        if (f.Protocol == "UDP") score += 35;
        if (f.RemotePort is >= 12000 and <= 14000) score += 100;
        else if (f.RemotePort is >= 11000 and <= 16000) score += 55;
        if (ControlPorts.Contains(f.RemotePort)) score -= 80;
        return score;
    }

    static void Publish(GameRouteLabV10Form form, List<Flow> candidates)
    {
        if (form.IsDisposed || !form.IsHandleCreated || candidates.Count == 0) return;
        form.BeginInvoke((Action)(() =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                var hidden = candidates.Where(x => !ControlPorts.Contains(x.RemotePort)).ToList();
                var best = hidden.Count > 0 ? hidden[0] : candidates[0];
                bool isHidden = !ControlPorts.Contains(best.RemotePort);

                targetIp = best.RemoteIp;
                targetPort = best.RemotePort;
                targetProtocol = best.Protocol;

                if (type.GetField("connectionText", flags)?.GetValue(form) is Label label)
                    label.Text = string.Join("\r\n", candidates.Take(10).Select(c =>
                        $"{c.Protocol,-3}  {c.RemoteIp}:{c.RemotePort,-5}  {(ControlPorts.Contains(c.RemotePort) ? "CONTROL" : "ROOM FLOW"),-10}  {c.In} IN / {c.Out} OUT"));

                type.GetField("endpoint", flags)?.SetValue(form, best.RemoteIp);
                type.GetField("endpointPort", flags)?.SetValue(form, best.RemotePort);
                if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box)
                    box.Text = $"{best.RemoteIp}:{best.RemotePort}";

                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                    metrics.Text = $"ENDPOINT   {best.RemoteIp}:{best.RemotePort}\r\nPROTOCOL   {best.Protocol}\r\nTRAFFIC    {best.In + best.Out} packets\r\nDIRECTION  {best.Out} out / {best.In} in\r\nSTATUS     {(isHidden ? "ACTUAL ROOM FLOW" : "CONTROL FLOW ONLY")}";

                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                {
                    quality.Text = $"● {(isHidden ? "ROOM FLOW FOUND" : "CONTROL ONLY")} • {best.Protocol} • {best.RemoteIp}:{best.RemotePort}";
                    quality.ForeColor = Color.FromArgb(40, 242, 122);
                }

                Log(form, $"[CROSSFIRE] V3 found {candidates.Count} bidirectional public flow(s).");
                Log(form, $"[CROSSFIRE] V3 selected {best.RemoteIp}:{best.RemotePort} {best.Protocol} • {best.Out} out / {best.In} in.");
                Log(form, isHidden
                    ? "[CROSSFIRE] ACTUAL ROOM FLOW confirmed; this endpoint is not one of the known control ports."
                    : "[CROSSFIRE] Only control transports were observed; no separate room flow was proven in this capture.");
            }
            catch (Exception ex) { Log(form, "[CROSSFIRE] V3 publish error: " + ex.Message); }
        }));
    }

    static HashSet<string> GetLocalIPv4s()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up))
                foreach (var a in n.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
                    set.Add(a.Address.ToString());
        }
        catch { }
        return set;
    }

    static bool IsPublic(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || b[0] >= 224) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
        return true;
    }

    static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.ASCII
            });
            if (p == null) return (-1, "");
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch { try { p.Kill(true); } catch { } }
            return (p.ExitCode, await output.ConfigureAwait(false) + "\r\n" + await error.ConfigureAwait(false));
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    static void Log(GameRouteLabV10Form form, string text)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }

    readonly record struct Flow(string RemoteIp, int RemotePort, string Protocol, int In, int Out);
}
