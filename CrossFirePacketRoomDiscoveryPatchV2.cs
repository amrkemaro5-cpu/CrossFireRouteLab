using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;

namespace CrossFireRouteLab;

/// <summary>
/// Packet-level CrossFire room/game transport discovery.
/// </summary>
internal static class CrossFirePacketRoomDiscoveryPatchV2
{
    static System.Threading.Timer? timer;
    static bool captureRunning;
    static DateTime lastCapture = DateTime.MinValue;
    static bool warnedElevation;
    static string roomTargetIp = "";
    static int roomTargetPort;
    static string roomTargetProtocol = "";

    static readonly HashSet<int> WebPorts = new() { 80, 443, 8080, 8443 };
    static readonly HashSet<int> CommonNoisePorts = new() { 53, 67, 68, 123, 1900, 3702, 5353, 5222, 3478, 5349 };

    public static bool TryGetRoomTarget(out string ip, out int port, out string protocol)
    {
        ip = roomTargetIp;
        port = roomTargetPort;
        protocol = roomTargetProtocol;
        return IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && port > 0 && protocol.Length > 0;
    }

    public static void Apply(GameRouteLabV10Form form)
    {
        if (form.IsDisposed) return;
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 3000, 4000);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        if (!IsAdministrator())
        {
            Log(form, "[CROSSFIRE] Room capture needs Game Route Lab to run as Administrator.");
            warnedElevation = true;
        }
        else
        {
            Log(form, "[CROSSFIRE] Room capture enabled: inspecting TCP/UDP packets, not just the socket table.");
        }
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated || captureRunning) return;
        if (DateTime.UtcNow - lastCapture < TimeSpan.FromSeconds(8)) return;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(GameRouteLabV10Form).GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        var gameName = typeof(GameRouteLabV10Form).GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;

        if (!IsAdministrator())
        {
            if (!warnedElevation)
            {
                Log(form, "[CROSSFIRE] Restart Game Route Lab as Administrator so packet capture can inspect the game transport.");
                warnedElevation = true;
            }
            return;
        }

        var current = ReadCurrentEndpoints(form);
        if (!current.Any(x => x.Port is 10009 or 13008 or 16666)) return;

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
            if (typeof(GameRouteLabV10Form).GetField("connections", flags)?.GetValue(form) is not System.Collections.IEnumerable list) return result;
            foreach (var item in list)
            {
                if (item == null) continue;
                var t = item.GetType();
                var ip = t.GetProperty("Ip", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item)?.ToString() ?? "";
                var port = Convert.ToInt32(t.GetProperty("Port", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item) ?? 0);
                var protocol = t.GetProperty("Protocol", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(item)?.ToString() ?? "TCP";
                if (IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && port > 0)
                    result.Add(new Endpoint(ip, port, protocol));
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
            Log(form, "[CROSSFIRE] Capturing live game traffic for 8 seconds… stay inside the room during this window.");
            await RunAsync("pktmon.exe", "filter", "remove").ConfigureAwait(false);
            var start = await RunAsync("pktmon.exe", "start", "--capture", "--comp", "nics", "--pkt-size", "256", "--file-name", etl, "--file-size", "32", "--log-mode", "circular").ConfigureAwait(false);
            if (start.ExitCode != 0)
            {
                Log(form, "[CROSSFIRE] PktMon could not start. Run Game Route Lab as Administrator.");
                return;
            }

            await Task.Delay(8000).ConfigureAwait(false);
            await RunAsync("pktmon.exe", "stop").ConfigureAwait(false);
            if (!File.Exists(etl))
            {
                Log(form, "[CROSSFIRE] No packet capture file was produced.");
                return;
            }

            var convert = await RunAsync("pktmon.exe", "etl2pcap", etl, "--out", pcap).ConfigureAwait(false);
            if (convert.ExitCode != 0 || !File.Exists(pcap))
            {
                Log(form, "[CROSSFIRE] PktMon capture could not be converted to PCAPNG.");
                return;
            }

            var flows = ParsePcap(pcap, GetLocalIPv4s());
            var knownKeys = new HashSet<string>(known.Select(x => $"{x.Protocol}|{x.Ip}:{x.Port}"), StringComparer.OrdinalIgnoreCase);

            var knownTraffic = flows
                .Where(x => knownKeys.Contains($"{x.Protocol}|{x.RemoteIp}:{x.RemotePort}"))
                .Where(x => x.Packets >= 4 && x.Inbound > 0 && x.Outbound > 0)
                .OrderByDescending(ScoreKnown)
                .Take(8)
                .ToList();

            var hidden = flows
                .Where(x => !knownKeys.Contains($"{x.Protocol}|{x.RemoteIp}:{x.RemotePort}"))
                .Where(IsUsefulHiddenCandidate)
                .OrderByDescending(ScoreHidden)
                .Take(12)
                .ToList();

            if (knownTraffic.Count == 0 && hidden.Count == 0)
            {
                Log(form, "[CROSSFIRE] No bidirectional CrossFire-like room flow was observed in this 8-second capture. Keep the match active for the next capture.");
                return;
            }

            Publish(form, knownTraffic.Concat(hidden).Take(12).ToList(), knownKeys);
        }
        catch (Exception ex)
        {
            Log(form, "[CROSSFIRE] Room capture error: " + ex.Message);
        }
        finally
        {
            try { await RunAsync("pktmon.exe", "stop").ConfigureAwait(false); } catch { }
            try { await RunAsync("pktmon.exe", "filter", "remove").ConfigureAwait(false); } catch { }
            captureRunning = false;
            TryDelete(etl);
            TryDelete(pcap);
        }
    }

    static double ScoreKnown(PacketFlow f)
    {
        double score = f.Packets + Math.Min(f.Bytes / 5000.0, 100);
        if (f.Inbound > 0 && f.Outbound > 0) score += 80;
        if (f.Protocol == "UDP") score += 20;
        if (f.RemotePort is 10009 or 13008 or 16666) score += 5;
        return score;
    }

    static bool IsUsefulHiddenCandidate(PacketFlow f)
    {
        if (!IsPublicIPv4(f.RemoteIp)) return false;
        if (WebPorts.Contains(f.RemotePort) || CommonNoisePorts.Contains(f.RemotePort)) return false;
        if (f.Packets < 6 || f.Inbound == 0 || f.Outbound == 0) return false;
        if (f.Protocol == "UDP" && f.RemotePort is >= 12000 and <= 16000) return true;
        if (f.Protocol == "TCP" && f.RemotePort >= 10000 && f.RemotePort <= 20000) return true;
        return f.RemotePort >= 1024;
    }

    static double ScoreHidden(PacketFlow f)
    {
        double score = f.Packets + Math.Min(f.Bytes / 4000.0, 120);
        if (f.Inbound > 0 && f.Outbound > 0) score += 100;
        if (f.Protocol == "UDP") score += 35;
        if (f.RemotePort is >= 12000 and <= 16000) score += 80;
        else if (f.RemotePort is >= 11000 and <= 17000) score += 40;
        else if (f.RemotePort >= 10000 && f.RemotePort <= 20000) score += 20;
        return score;
    }

    static void Publish(GameRouteLabV10Form form, List<PacketFlow> candidates, HashSet<string> knownKeys)
    {
        if (form.IsDisposed || !form.IsHandleCreated || candidates.Count == 0) return;
        form.BeginInvoke((Action)(() =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                var best = candidates[0];
                var bestKey = $"{best.Protocol}|{best.RemoteIp}:{best.RemotePort}";
                var hidden = candidates.Where(x => !knownKeys.Contains($"{x.Protocol}|{x.RemoteIp}:{x.RemotePort}")).ToList();
                roomTargetIp = best.RemoteIp;
                roomTargetPort = best.RemotePort;
                roomTargetProtocol = best.Protocol;

                if (type.GetField("connectionText", flags)?.GetValue(form) is Label label)
                {
                    label.Text = string.Join("\r\n", candidates.Take(8).Select(c =>
                        $"{c.Protocol,-3}  {c.RemoteIp}:{c.RemotePort,-5}  {(knownKeys.Contains($"{c.Protocol}|{c.RemoteIp}:{c.RemotePort}") ? "KNOWN FLOW" : "ROOM FLOW")}  {c.Packets} pkts"));
                }

                type.GetField("endpoint", flags)?.SetValue(form, best.RemoteIp);
                type.GetField("endpointPort", flags)?.SetValue(form, best.RemotePort);
                if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box)
                    box.Text = $"{best.RemoteIp}:{best.RemotePort}";

                var kind = knownKeys.Contains(bestKey) ? "SAME TRANSPORT" : "HIDDEN ROOM FLOW";
                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                {
                    metrics.Text = $"ENDPOINT   {best.RemoteIp}:{best.RemotePort}\r\nPROTOCOL   {best.Protocol}\r\nTRAFFIC    {best.Packets} packets\r\nDIRECTION  {best.Outbound} out / {best.Inbound} in\r\nBYTES      {best.Bytes:N0}\r\nSTATUS     {kind}";
                }

                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                {
                    quality.Text = $"● {kind} • {best.Protocol} • {best.Packets} PKTS";
                    quality.ForeColor = Color.FromArgb(40, 242, 122);
                }

                Log(form, $"[CROSSFIRE] Room capture found {candidates.Count} useful flow(s): {candidates.Count(x => x.Protocol == "TCP")} TCP, {candidates.Count(x => x.Protocol == "UDP")} UDP.");
                Log(form, $"[CROSSFIRE] Best game-flow candidate: {best.RemoteIp}:{best.RemotePort} {best.Protocol} • {best.Packets} packets • {best.Bytes:N0} bytes • {best.Outbound} out / {best.Inbound} in.");
                if (hidden.Count > 0)
                    Log(form, $"[CROSSFIRE] Hidden flow candidates: {hidden.Count}; UDP 12000-16000 candidates receive priority because CrossFire support material documents dynamic UDP game ports in this range.");
            }
            catch (Exception ex)
            {
                Log(form, "[CROSSFIRE] Room-flow publish error: " + ex.Message);
            }
        }));
    }

    static HashSet<string> GetLocalIPv4s()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up))
                foreach (var a in n.GetIPProperties().UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork))
                    set.Add(a.Address.ToString());
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
            uint blockType = LE32(data, offset);
            uint blockLength = LE32(data, offset + 4);
            if (blockLength < 12 || offset + blockLength > data.Length) break;

            if (blockType == 0x00000001 && blockLength >= 20)
            {
                interfaces[(uint)interfaces.Count] = LE16(data, offset + 8);
            }
            else if (blockType == 0x00000006 && blockLength >= 32)
            {
                uint interfaceId = LE32(data, offset + 8);
                uint captured = LE32(data, offset + 20);
                int packetOffset = offset + 28;
                if (captured > 0 && packetOffset + captured <= offset + (int)blockLength - 4 && interfaces.TryGetValue(interfaceId, out var linkType))
                {
                    var parsed = ParsePacket(data, packetOffset, (int)captured, linkType, localIps);
                    if (parsed is PacketFlow flow)
                    {
                        var key = $"{flow.Protocol}|{flow.RemoteIp}|{flow.RemotePort}";
                        if (flows.TryGetValue(key, out var old))
                            flows[key] = old with { Packets = old.Packets + 1, Bytes = old.Bytes + flow.Bytes, Inbound = old.Inbound + flow.Inbound, Outbound = old.Outbound + flow.Outbound };
                        else
                            flows[key] = flow;
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
        ushort ether = BE16(d, o + 12);
        if (ether == 0x8100 && len >= 38)
        {
            ether = BE16(d, o + 16);
            ip += 4;
        }
        if (ether != 0x0800 || ip + 20 > o + len) return null;

        int ihl = (d[ip] & 0x0F) * 4;
        if (ihl < 20 || ip + ihl > o + len) return null;

        byte proto = d[ip + 9];
        string src = new IPAddress(new ReadOnlySpan<byte>(d, ip + 12, 4)).ToString();
        string dst = new IPAddress(new ReadOnlySpan<byte>(d, ip + 16, 4)).ToString();
        bool outbound = localIps.Contains(src);
        bool inbound = localIps.Contains(dst);
        if (!outbound && !inbound) return null;

        int transport = ip + ihl;
        if (transport + 4 > o + len) return null;
        string remote = outbound ? dst : src;
        int remotePort = outbound ? BE16(d, transport + 2) : BE16(d, transport);

        if (proto == 17)
            return new PacketFlow(remote, remotePort, "UDP", 1, len, inbound ? 1 : 0, outbound ? 1 : 0);
        if (proto != 6 || transport + 20 > o + len) return null;
        return new PacketFlow(remote, remotePort, "TCP", 1, len, inbound ? 1 : 0, outbound ? 1 : 0);
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

    static ushort LE16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));
    static uint LE32(byte[] d, int o) => (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
    static ushort BE16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);

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
            p.StartInfo = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
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

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    readonly record struct Endpoint(string Ip, int Port, string Protocol);
    readonly record struct PacketFlow(string RemoteIp, int RemotePort, string Protocol, int Packets, long Bytes, int Inbound, int Outbound);
    readonly record struct RunResult(int ExitCode, string Output);
}
