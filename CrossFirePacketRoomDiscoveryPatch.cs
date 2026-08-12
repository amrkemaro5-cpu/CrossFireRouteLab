using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

/// <summary>
/// Packet-level fallback for CrossFire room discovery.
/// netstat can only show sockets that Windows associates with a PID. CrossFire
/// can keep its room traffic outside that view, especially for UDP/dynamic
/// transports. This patch uses the Windows-inbox PktMon capture at the NIC,
/// then identifies public remote TCP/UDP flows that appear during live traffic.
/// No routes, DNS, firewall rules or router settings are changed.
/// </summary>
internal static class CrossFirePacketRoomDiscoveryPatch
{
    static System.Threading.Timer? timer;
    static readonly object sync = new();
    static bool captureRunning;
    static DateTime lastCapture = DateTime.MinValue;
    static bool warnedElevation;
    static readonly HashSet<int> WebPorts = new() { 80, 443, 8080, 8443 };
    static readonly HashSet<int> CommonNoisePorts = new() { 53, 123, 1900, 5353, 5222, 3478, 5349 };

    public static void Apply(GameRouteLabV10Form form)
    {
        if (form.IsDisposed) return;
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 2500, 3500);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        if (!IsAdministrator())
        {
            Log(form, "[CROSSFIRE] Packet room discovery is available but needs Game Route Lab to run as Administrator for PktMon capture.");
            warnedElevation = true;
        }
        else
        {
            Log(form, "[CROSSFIRE] Packet-level room discovery enabled (PktMon fallback for hidden CrossFire transports).");
        }
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated || captureRunning) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(GameRouteLabV10Form).GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        var gameName = typeof(GameRouteLabV10Form).GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;
        if (!IsAdministrator())
        {
            if (!warnedElevation) { Log(form, "[CROSSFIRE] Hidden room traffic requires PktMon; restart Game Route Lab as Administrator for packet discovery."); warnedElevation = true; }
            return;
        }
        if (DateTime.UtcNow - lastCapture < TimeSpan.FromSeconds(12)) return;

        var current = ReadCurrentEndpoints(form);
        var hasOnlyMaster = current.Count <= 1 && current.Any(x => x.Port == 10009 || x.Port == 13008);
        if (!hasOnlyMaster) return;

        lastCapture = DateTime.UtcNow;
        captureRunning = true;
        _ = Task.Run(() => CaptureAndPublish(form, current));
    }

    static List<Endpoint> ReadCurrentEndpoints(GameRouteLabV10Form form)
    {
        var result = new List<Endpoint>();
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var list = typeof(GameRouteLabV10Form).GetField("connections", flags)?.GetValue(form) as System.Collections.IEnumerable;
            if (list == null) return result;
            foreach (var item in list)
            {
                if (item == null) continue;
                var t = item.GetType();
                var ip = t.GetProperty("Ip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item)?.ToString() ?? "";
                var portObj = t.GetProperty("Port", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item);
                var protocol = t.GetProperty("Protocol", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item)?.ToString() ?? "";
                if (int.TryParse(portObj?.ToString(), out var port) && IPAddress.TryParse(ip, out _)) result.Add(new Endpoint(ip, port, protocol));
            }
        }
        catch { }
        return result;
    }

    static async Task CaptureAndPublish(GameRouteLabV10Form form, List<Endpoint> known)
    {
        string root = Path.Combine(Path.GetTempPath(), "GameRouteLab", "CrossFireRoomCapture");
        Directory.CreateDirectory(root);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
        string etl = Path.Combine(root, $"room-{stamp}.etl");
        string pcap = Path.Combine(root, $"room-{stamp}.pcapng");
        try
        {
            Log(form, "[CROSSFIRE] Capturing NIC traffic for 6 seconds to find the room transport…");
            var start = await RunAsync("pktmon.exe", "start", "--capture", "--comp", "nics", "--pkt-size", "128", "--file-name", etl, "--file-size", "16", "--log-mode", "circular").ConfigureAwait(false);
            if (start.ExitCode != 0)
            {
                Log(form, "[CROSSFIRE] PktMon could not start. Run Game Route Lab as Administrator and retry inside a room.");
                return;
            }

            await Task.Delay(6000).ConfigureAwait(false);
            await RunAsync("pktmon.exe", "stop").ConfigureAwait(false);
            if (!File.Exists(etl))
            {
                Log(form, "[CROSSFIRE] PktMon stopped without producing a capture file.");
                return;
            }

            var convert = await RunAsync("pktmon.exe", "etl2pcap", etl, "--out", pcap).ConfigureAwait(false);
            if (convert.ExitCode != 0 || !File.Exists(pcap))
            {
                Log(form, "[CROSSFIRE] PktMon capture was created but could not be converted to PCAPNG.");
                return;
            }

            var localIps = GetLocalIPv4s();
            var flows = ParsePcap(pcap, localIps);
            var knownKeys = new HashSet<string>(known.Select(x => $"{x.Ip}:{x.Port}"), StringComparer.OrdinalIgnoreCase);
            var candidates = flows
                .Where(x => !knownKeys.Contains($"{x.RemoteIp}:{x.RemotePort}"))
                .Where(x => IsUsefulCandidate(x))
                .OrderByDescending(Score)
                .Take(12)
                .ToList();

            if (candidates.Count == 0)
            {
                Log(form, "[CROSSFIRE] Packet capture found no additional public CrossFire-like room transport. The traffic may be encrypted/multiplexed or exposed through another Windows component.");
                return;
            }

            Publish(form, candidates);
        }
        catch (Exception ex)
        {
            Log(form, "[CROSSFIRE] Packet discovery error: " + ex.Message);
        }
        finally
        {
            captureRunning = false;
            TryDelete(etl); TryDelete(pcap);
        }
    }

    static bool IsUsefulCandidate(PacketFlow f)
    {
        if (!IsPublicIPv4(f.RemoteIp)) return false;
        if (WebPorts.Contains(f.RemotePort) || CommonNoisePorts.Contains(f.RemotePort)) return false;
        if (f.Packets < 3) return false;
        if (f.RemotePort is < 1024 and not 10009 and not 13008 and not 16666) return false;
        return true;
    }

    static double Score(PacketFlow f)
    {
        double score = f.Packets;
        if (f.Inbound > 0 && f.Outbound > 0) score += 25;
        if (f.RemotePort is 10009 or 13008 or 16666) score += 80;
        else if (f.RemotePort >= 11000 && f.RemotePort <= 16000) score += 55;
        else if (f.RemotePort >= 10000 && f.RemotePort <= 20000) score += 25;
        if (f.Protocol == "UDP") score += 8;
        return score;
    }

    static void Publish(GameRouteLabV10Form form, List<PacketFlow> candidates)
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
                    var itemType = listObj.GetType().GetGenericArguments().FirstOrDefault();
                    if (itemType != null)
                    {
                        foreach (var c in candidates)
                        {
                            var item = Activator.CreateInstance(itemType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                                new object[] { c.RemoteIp, c.RemotePort, c.Protocol, $"ROOM TRAFFIC • {c.Packets} pkts" }, null);
                            if (item != null) list.Add(item);
                        }
                    }
                }

                var label = type.GetField("connectionText", flags)?.GetValue(form) as Label;
                if (label != null) label.Text = string.Join("\r\n", candidates.Take(6).Select(c => $"{c.Protocol,-3}  {c.RemoteIp}:{c.RemotePort,-5}  ROOM TRAFFIC  {c.Packets} pkts"));

                var best = candidates.First();
                type.GetField("endpoint", flags)?.SetValue(form, best.RemoteIp);
                type.GetField("endpointPort", flags)?.SetValue(form, best.RemotePort);
                if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{best.RemoteIp}:{best.RemotePort}";
                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                    metrics.Text = $"ENDPOINT   {best.RemoteIp}:{best.RemotePort}\r\nPROTOCOL   {best.Protocol}\r\nLATENCY    room transport discovered\r\nTRAFFIC    {best.Packets} packets\r\nDIRECTION  {best.Outbound} out / {best.Inbound} in\r\nSTATUS     ROOM CANDIDATE";
                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                {
                    quality.Text = $"● ROOM TRANSPORT FOUND • {best.Protocol} • {best.Packets} PKTS";
                    quality.ForeColor = Color.FromArgb(40, 242, 122);
                }

                var tcp = candidates.Count(x => x.Protocol == "TCP");
                var udp = candidates.Count(x => x.Protocol == "UDP");
                Log(form, $"[CROSSFIRE] Packet discovery found {candidates.Count} additional room-flow candidate(s): {tcp} TCP, {udp} UDP.");
                Log(form, $"[CROSSFIRE] Best packet candidate: {best.RemoteIp}:{best.RemotePort} {best.Protocol} • {best.Packets} packets • {best.Outbound} out / {best.Inbound} in.");
                Log(form, "[CROSSFIRE] This endpoint was discovered from live packet traffic, not from the 10009 master socket.");
            }
            catch (Exception ex) { Log(form, "[CROSSFIRE] Packet publish error: " + ex.Message); }
        }));
    }

    static HashSet<string> GetLocalIPv4s()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up))
                foreach (var a in n.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)) set.Add(a.Address.ToString());
        }
        catch { }
        return set;
    }

    static List<PacketFlow> ParsePcap(string path, HashSet<string> localIps)
    {
        var data = File.ReadAllBytes(path);
        var flows = new Dictionary<string, PacketFlow>(StringComparer.OrdinalIgnoreCase);
        int offset = 0;
        var interfaces = new Dictionary<uint, ushort>();
        while (offset + 12 <= data.Length)
        {
            uint blockType = U32(data, offset);
            uint blockLength = U32(data, offset + 4);
            if (blockLength < 12 || offset + blockLength > data.Length) break;
            if (blockType == 0x00000001 && blockLength >= 20)
            {
                uint id = (uint)interfaces.Count;
                interfaces[id] = U16(data, offset + 8);
            }
            else if (blockType == 0x00000006 && blockLength >= 32)
            {
                uint id = U32(data, offset + 8);
                uint captured = U32(data, offset + 20);
                int packetOffset = offset + 28;
                if (captured > 0 && packetOffset + captured <= offset + blockLength - 4 && interfaces.TryGetValue(id, out var linkType))
                {
                    var flow = ParsePacket(data, packetOffset, (int)captured, linkType, localIps);
                    if (flow != null)
                    {
                        var key = $"{flow.Protocol}|{flow.RemoteIp}|{flow.RemotePort}";
                        if (flows.TryGetValue(key, out var old))
                            flows[key] = old with { Packets = old.Packets + 1, Bytes = old.Bytes + flow.Bytes, Inbound = old.Inbound + flow.Inbound, Outbound = old.Outbound + flow.Outbound };
                        else flows[key] = flow;
                    }
                }
            }
            offset += (int)blockLength;
        }
        return flows.Values.ToList();
    }

    static PacketFlow? ParsePacket(byte[] d, int o, int len, ushort linkType, HashSet<string> localIps)
    {
        if (linkType != 1 || len < 34) return null;
        int ip = o;
        ushort ether = U16(d, o + 12);
        if (ether == 0x8100 && len >= 38) { ether = U16(d, o + 16); ip += 4; }
        if (ether != 0x0800 || ip + 20 > o + len) return null;
        int ihl = (d[ip] & 0x0F) * 4;
        if (ihl < 20 || ip + ihl > o + len) return null;
        byte proto = d[ip + 9];
        string src = new IPAddress(new ReadOnlySpan<byte>(d, ip + 12, 4)).ToString();
        string dst = new IPAddress(new ReadOnlySpan<byte>(d, ip + 16, 4)).ToString();
        int transport = ip + ihl;
        if (transport + 4 > o + len || (!localIps.Contains(src) && !localIps.Contains(dst))) return null;

        string remote; int port; string protocol;
        if (proto == 6)
        {
            if (transport + 4 > o + len) return null;
            int srcPort = U16(d, transport); int dstPort = U16(d, transport + 2);
            if (localIps.Contains(src)) { remote = dst; port = dstPort; }
            else { remote = src; port = srcPort; }
            protocol = "TCP";
        }
        else if (proto == 17)
        {
            if (transport + 4 > o + len) return null;
            int srcPort = U16(d, transport); int dstPort = U16(d, transport + 2);
            if (localIps.Contains(src)) { remote = dst; port = dstPort; }
            else { remote = src; port = srcPort; }
            protocol = "UDP";
        }
        else return null;

        bool outbound = localIps.Contains(src);
        return new PacketFlow(remote, port, protocol, 1, len, outbound ? 0 : 1, outbound ? 1 : 0);
    }

    static bool IsPublicIPv4(string value)
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

    static ushort U16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
    static uint U32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

    static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    static async Task<RunResult> RunAsync(string file, params string[] args)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) p.StartInfo.ArgumentList.Add(arg);
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().ConfigureAwait(false);
            return new RunResult(p.ExitCode, (await stdout.ConfigureAwait(false)) + "\r\n" + (await stderr.ConfigureAwait(false)));
        }
        catch (Exception ex) { return new RunResult(-1, ex.Message); }
    }

    static void Log(GameRouteLabV10Form form, string text)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }

    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    readonly record struct Endpoint(string Ip, int Port, string Protocol);
    readonly record struct PacketFlow(string RemoteIp, int RemotePort, string Protocol, int Packets, long Bytes, int Inbound, int Outbound);
    readonly record struct RunResult(int ExitCode, string Output);
}
