using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

/// <summary>
/// Authoritative CrossFire room-flow probe. It deliberately bypasses the socket-table
/// ranking code and inspects Windows PktMon flow captures instead. This is intended to
/// answer one question first: which public IP:port actually carries bidirectional room traffic?
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
    static readonly Regex Pair = new(
        @"(?<src>(?:\d{1,3}\.){3}\d{1,3})[:\.](?<sport>\d{1,5})\s*(?:>|->)\s*(?<dst>(?:\d{1,3}\.){3}\d{1,3})[:\.](?<dport>\d{1,5})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool TryGetTarget(out string ip, out int port, out string protocol)
    {
        ip = targetIp; port = targetPort; protocol = targetProtocol;
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
        Log(form, "[CROSSFIRE] V3 authoritative room probe enabled. Socket-table ranking is disabled; PktMon flow capture is now the source of truth.");
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
        string txt = Path.Combine(root, $"room-{stamp}.txt");

        try
        {
            Log(form, "[CROSSFIRE] V3 capturing flowing NIC packets for 12 seconds. Stay inside the active match.");
            await RunAsync("pktmon.exe", "filter remove", 3000).ConfigureAwait(false);
            var start = await RunAsync("pktmon.exe", $"start --capture --comp nics --type flow --pkt-size 0 --file-name {Quote(etl)} --file-size 64 --log-mode circular", 5000).ConfigureAwait(false);
            if (start.ExitCode != 0)
            {
                Log(form, "[CROSSFIRE] V3 could not start PktMon. Run GRL as Administrator.");
                return;
            }

            await Task.Delay(12000).ConfigureAwait(false);
            await RunAsync("pktmon.exe", "stop", 5000).ConfigureAwait(false);
            if (!File.Exists(etl))
            {
                Log(form, "[CROSSFIRE] V3 capture produced no ETL file.");
                return;
            }

            var converted = await RunAsync("pktmon.exe", $"etl2txt {Quote(etl)} --out {Quote(txt)} --verbose 1", 12000).ConfigureAwait(false);
            if (converted.ExitCode != 0 || !File.Exists(txt))
            {
                Log(form, "[CROSSFIRE] V3 could not convert the PktMon capture to text.");
                return;
            }

            var flows = ParseText(File.ReadAllText(txt), GetLocalIPv4s());
            var candidates = flows.Values
                .Where(x => x.In > 0 && x.Out > 0 && IsPublic(x.RemoteIp) && !NoisePorts.Contains(x.RemotePort))
                .OrderByDescending(Score)
                .Take(20)
                .ToList();

            if (candidates.Count == 0)
            {
                Log(form, "[CROSSFIRE] V3 saw packets but found no bidirectional public IP:port flow yet. The raw diagnostic capture was retained temporarily for inspection.");
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
            TryDelete(txt);
        }
    }

    static Dictionary<string, Flow> ParseText(string text, HashSet<string> locals)
    {
        var result = new Dictionary<string, Flow>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var m = Pair.Match(line);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups[2].Value, out var sp) || !int.TryParse(m.Groups[4].Value, out var dp)) continue;
            var src = m.Groups[1].Value;
            var dst = m.Groups[3].Value;
            bool srcLocal = locals.Contains(src);
            bool dstLocal = locals.Contains(dst);
            if (srcLocal == dstLocal) continue;

            string remote = srcLocal ? dst : src;
            int remotePort = srcLocal ? dp : sp;
            if (!IsPublic(remote) || remotePort <= 0) continue;
            string protocol = line.Contains("UDP", StringComparison.OrdinalIgnoreCase) ? "UDP" :
                              line.Contains("TCP", StringComparison.OrdinalIgnoreCase) ? "TCP" : "IP";
            if (protocol == "IP") continue;

            string key = $"{protocol}|{remote}:{remotePort}";
            if (!result.TryGetValue(key, out var flow))
                flow = new Flow(remote, remotePort, protocol, 0, 0);

            if (srcLocal) flow = flow with { Out = flow.Out + 1 };
            else flow = flow with { In = flow.In + 1 };
            result[key] = flow;
        }
        return result;
    }

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
                var best = hidden.FirstOrDefault() ?? candidates[0];
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
                if (isHidden)
                    Log(form, "[CROSSFIRE] This is a NEW room flow, not TCP/10009. Route measurement can now target the actual room transport.");
                else
                    Log(form, "[CROSSFIRE] Only control transports were observed; no separate room flow was proven in this capture.");
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
