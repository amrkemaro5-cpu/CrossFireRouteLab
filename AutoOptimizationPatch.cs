using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Final evidence-driven diagnosis and low-risk host optimization layer.
///
/// Important design rule: the application must not pretend that a generic ICMP
/// or TCP probe is the same thing as CrossFire's in-game ping. The game ping is
/// the primary user-facing target; GRL's probes are supporting evidence used to
/// locate the delay (PC -> gateway -> upstream -> server).
///
/// Automatic changes are deliberately limited to reversible Windows settings
/// that are safe to correct when they are visibly degraded:
///   - TCP receive-window autotuning -> Normal
///   - TCP RSS -> Enabled
///   - Winsock send autotuning -> On
///   - CrossFire process priority -> AboveNormal (never High/Realtime)
///   - Prefer a healthy physical Ethernet interface over Wi-Fi only when Windows
///     exposes multiple physical default routes and no VPN/TAP route is present.
///
/// DNS, firewall rules, VPN/TAP adapters, persistent routes, router settings,
/// PPPoE settings and firmware are never changed automatically. Those controls
/// cannot be used to manufacture a 40-50 ms CrossFire path when WE/server
/// peering is the actual bottleneck.
/// </summary>
internal static class AutoOptimizationPatch
{
    static readonly TimeSpan Warmup = TimeSpan.FromSeconds(15);
    static readonly TimeSpan Recheck = TimeSpan.FromMinutes(4);
    static readonly TimeSpan VerificationDelay = TimeSpan.FromSeconds(10);
    static DateTime _startedUtc;
    static DateTime _lastRunUtc = DateTime.MinValue;
    static bool _running;
    static System.Threading.Timer? _timer;

    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        _startedUtc = DateTime.UtcNow;
        _timer = new System.Threading.Timer(_ => Tick(form), null, 4500, 4500);
        form.FormClosed += (_, _) =>
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
        };
    }

    static void Tick(Form form)
    {
        if (_running || form.IsDisposed || !form.IsHandleCreated) return;
        if (DateTime.UtcNow - _startedUtc < Warmup) return;
        if (DateTime.UtcNow - _lastRunUtc < Recheck) return;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();
        var pidValue = type.GetField("gamePid", flags)?.GetValue(form);
        var endpoint = type.GetField("endpoint", flags)?.GetValue(form) as string;
        var portValue = type.GetField("endpointPort", flags)?.GetValue(form);
        var pingHistory = type.GetField("pingHistory", flags)?.GetValue(form) as System.Collections.ICollection;
        var gameName = type.GetField("gameName", flags)?.GetValue(form) as string ?? "";

        if (pidValue is not int pid || pid <= 0 || string.IsNullOrWhiteSpace(endpoint) || pingHistory == null || pingHistory.Count < 10)
            return;

        if (IsPrivateOrLoopback(endpoint)) return;

        _running = true;
        _lastRunUtc = DateTime.UtcNow;
        _ = Task.Run(() => DiagnoseAndOptimize(form, pid, gameName, endpoint!, portValue is int p ? p : 0));
    }

    static async Task DiagnoseAndOptimize(Form form, int pid, string gameName, string endpoint, int port)
    {
        var applied = new List<string>();
        var skipped = new List<string>();
        try
        {
            Log(form, "[AI] FINAL DIAGNOSIS: collecting Windows, interface, gateway and endpoint evidence…");

            var tcpBefore = await RunAsync("netsh.exe", "interface tcp show global", 6000);
            var winsockBefore = await RunAsync("netsh.exe", "winsock show autotuning", 5000);
            var routesBefore = await RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command \"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore ActiveStore | Select-Object InterfaceIndex,NextHop,RouteMetric | ConvertTo-Json -Compress\"", 7000);
            var interfaces = await ReadInterfaces();

            var endpointSamples = await ProbeSeries(endpoint, port, 10);
            var endpointOk = endpointSamples.Where(x => x >= 0).ToList();
            var loss = endpointSamples.Count == 0 ? 1.0 : 1.0 - (double)endpointOk.Count / endpointSamples.Count;
            var avg = endpointOk.Count == 0 ? -1 : endpointOk.Average();
            var min = endpointOk.Count == 0 ? -1 : endpointOk.Min();
            var max = endpointOk.Count == 0 ? -1 : endpointOk.Max();
            var spread = endpointOk.Count == 0 ? -1 : max - min;

            var gateway = ExtractGateway(form);
            var gatewaySamples = string.IsNullOrWhiteSpace(gateway) ? new List<double>() : await ProbeSeries(gateway!, 0, 5);
            var gatewayOk = gatewaySamples.Where(x => x >= 0).ToList();
            var gatewayAvg = gatewayOk.Count == 0 ? -1 : gatewayOk.Average();

            var diagnosis = Classify(avg, loss, spread, gatewayAvg, interfaces);
            Log(form, "[AI] " + diagnosis);
            Log(form, $"[AI] GRL probe endpoint {endpoint}:{port} | avg {(avg < 0 ? "—" : avg.ToString("0"))} ms | loss {(loss * 100):0.#}% | spread {(spread < 0 ? "—" : spread.ToString("0"))} ms.");
            if (gatewayAvg >= 0)
                Log(form, $"[AI] Local gateway {gateway} | avg {gatewayAvg:0.#} ms.");

            var userIsp = DetectIspHint(form);
            if (!string.IsNullOrWhiteSpace(userIsp))
                Log(form, "[AI] ISP profile: " + userIsp + ".");

            if (IsCrossFire(gameName))
            {
                try
                {
                    using var game = Process.GetProcessById(pid);
                    if (game.PriorityClass is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Normal)
                    {
                        game.PriorityClass = ProcessPriorityClass.AboveNormal;
                        applied.Add("CrossFire process priority → AboveNormal");
                        Log(form, "[AI] APPLIED: CrossFire process priority → AboveNormal.");
                    }
                }
                catch (Exception ex)
                {
                    skipped.Add("CrossFire priority (administrator/access restriction)");
                    Log(form, "[AI] SKIPPED: CrossFire priority could not be changed: " + ex.Message);
                }
            }

            // These are global TCP settings, so we only correct them when the
            // current state is visibly non-standard/degraded. Microsoft documents
            // Normal autotuning and enabled RSS as supported netsh states.
            var autotune = ReadValue(tcpBefore, "Receive Window Auto-Tuning Level");
            var rss = ReadValue(tcpBefore, "Receive-Side Scaling State");
            if (NeedsNormalAutotuning(autotune) || NeedsEnabled(rss))
            {
                var change = await RunAsync("netsh.exe", "interface tcp set global autotuninglevel=normal rss=enabled", 7000);
                if (LooksSuccessful(change))
                {
                    applied.Add("TCP autotuning=Normal, RSS=Enabled");
                    Log(form, "[AI] APPLIED: restored the Windows TCP baseline (autotuning=Normal, RSS=Enabled).");
                }
                else
                {
                    skipped.Add("TCP baseline (administrator permission required)");
                    Log(form, "[AI] SKIPPED: Windows rejected the TCP baseline correction; no fallback registry edits were attempted.");
                }
            }
            else
            {
                Log(form, "[AI] TCP baseline: healthy; no global TCP tuning needed.");
            }

            if (WinsockAutotuningDisabled(winsockBefore))
            {
                var change = await RunAsync("netsh.exe", "winsock set autotuning on", 7000);
                if (LooksSuccessful(change))
                {
                    applied.Add("Winsock send autotuning → On");
                    Log(form, "[AI] APPLIED: Winsock send autotuning → On.");
                }
                else
                {
                    skipped.Add("Winsock autotuning (administrator permission required)");
                    Log(form, "[AI] SKIPPED: Winsock autotuning could not be changed.");
                }
            }
            else
            {
                Log(form, "[AI] Winsock send autotuning: healthy or not explicitly reported as disabled.");
            }

            // Interface metric changes are only made when the machine exposes
            // multiple physical default routes. VPN/TAP/Wintun routes are left
            // untouched because changing them automatically can break a VPN.
            var routeDecision = await EvaluatePhysicalInterfaceRoutes(interfaces, routesBefore);
            if (routeDecision.ApplyCommand != null)
            {
                var change = await RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(routeDecision.ApplyCommand), 8000);
                if (LooksSuccessful(change))
                {
                    applied.Add(routeDecision.Description);
                    Log(form, "[AI] APPLIED: " + routeDecision.Description + ".");
                }
                else
                {
                    skipped.Add("physical-interface metric correction");
                    Log(form, "[AI] SKIPPED: interface metric correction was not accepted; no adapter was disabled.");
                }
            }
            else if (!string.IsNullOrWhiteSpace(routeDecision.Description))
            {
                Log(form, "[AI] ROUTE: " + routeDecision.Description + ".");
            }

            if (gatewayAvg >= 0 && avg >= 0 && gatewayAvg <= 5 && avg >= gatewayAvg + 50)
            {
                Log(form, "[AI] PATH FINDING: the home gateway is healthy; most measured delay is beyond the LAN. DNS/registry tweaks cannot manufacture a shorter WE→server path.");
            }
            else if (gatewayAvg > 5)
            {
                Log(form, "[AI] PATH FINDING: the gateway itself is adding delay. Fix the Ethernet/router/local-link condition before chasing Internet routing.");
            }

            if (loss >= 0.10)
                Log(form, "[AI] PATH FINDING: elevated probe loss detected. GRL will not equate this with CrossFire packet loss without game-level evidence.");
            else if (avg >= 0 && spread < 15)
                Log(form, "[AI] PATH FINDING: the GRL probe path is stable. A large difference from CrossFire's in-game ping means this probe is not a valid game-ping surrogate.");

            Log(form, "[AI] TARGET: 40–50+ ms is treated as a goal, not a promise. A local PC cannot force WE or the game server to use a physically/peering-shorter path.");
            Log(form, applied.Count == 0
                ? "[AI] FINAL DIAGNOSIS: no safe automatic change was justified."
                : "[AI] FINAL DIAGNOSIS: applied " + string.Join("; ", applied) + ".");
            if (skipped.Count > 0)
                Log(form, "[AI] NOT APPLIED: " + string.Join("; ", skipped) + ".");

            Log(form, "[AI] SAFETY: no DNS, persistent route, firewall, VPN/TAP, PPPoE, router or firmware change was made automatically.");

            if (applied.Count > 0)
            {
                await Task.Delay(VerificationDelay).ConfigureAwait(false);
                var tcpAfter = await RunAsync("netsh.exe", "interface tcp show global", 5000);
                var winAfter = await RunAsync("netsh.exe", "winsock show autotuning", 5000);
                Log(form, $"[AI] VERIFY: TCP baseline {(SameHealthyBaseline(tcpAfter) ? "healthy" : "review needed")}; Winsock {(WinsockAutotuningDisabled(winAfter) ? "still reports disabled" : "not disabled")}.");
            }
        }
        catch (Exception ex)
        {
            Log(form, "[AI] FINAL DIAGNOSIS stopped safely: " + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    static string Classify(double avg, double loss, double spread, double gatewayAvg, IReadOnlyList<InterfaceInfo> interfaces)
    {
        if (interfaces.Count == 0) return "No usable network interface inventory was returned; diagnosis remains observational.";
        if (loss >= .10) return "Probe quality is degraded by elevated loss; route changes are not justified until the loss source is confirmed.";
        if (gatewayAvg > 5) return "Local-link warning: the gateway itself is adding measurable latency.";
        if (avg >= 0 && gatewayAvg >= 0 && avg >= gatewayAvg + 50) return "Stable local link, but substantial delay is being measured beyond the gateway.";
        if (spread >= 30) return "Latency is variable; continued observation is warranted before changing the network.";
        return "Stable route evidence; only conservative host-side corrections are eligible.";
    }

    static string DetectIspHint(Form form)
    {
        try
        {
            var field = form.GetType().GetField("lastNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
            var text = field?.GetValue(form) as string ?? "";
            if (Regex.IsMatch(text, @"\b8452\b", RegexOptions.IgnoreCase)) return "WE / Telecom Egypt (AS8452)";
            if (text.Contains("WE", StringComparison.OrdinalIgnoreCase) || text.Contains("Telecom Egypt", StringComparison.OrdinalIgnoreCase)) return "WE / Telecom Egypt";
        }
        catch { }
        return "";
    }

    static async Task<RouteDecision> EvaluatePhysicalInterfaceRoutes(IReadOnlyList<InterfaceInfo> interfaces, string routeJson)
    {
        try
        {
            if (interfaces.Any(i => i.IsVirtualOrVpn && i.HasDefaultRoute))
                return new RouteDecision(null, "a VPN/TAP/Wintun default route exists; left interface metrics untouched");

            var physical = interfaces.Where(i => i.HasDefaultRoute && i.IsPhysical && i.Gateway != "").ToList();
            if (physical.Count < 2)
                return new RouteDecision(null, "one physical default route is active; there is no alternate local interface to optimize");

            var ethernet = physical.FirstOrDefault(i => i.IsEthernet);
            var wifi = physical.FirstOrDefault(i => i.IsWifi);
            if (ethernet == null || wifi == null)
                return new RouteDecision(null, "multiple physical routes exist, but no safe Ethernet-vs-Wi-Fi preference could be established");

            var ethPing = await ProbeGateway(ethernet.Gateway);
            var wifiPing = await ProbeGateway(wifi.Gateway);
            if (ethPing < 0 || wifiPing < 0)
                return new RouteDecision(null, "multiple physical routes exist, but gateway comparison was inconclusive");

            if (ethPing <= 5 && wifiPing >= ethPing + 3 && ethernet.InterfaceMetric > wifi.InterfaceMetric)
            {
                var command = $"Set-NetIPInterface -InterfaceIndex {ethernet.InterfaceIndex} -AddressFamily IPv4 -InterfaceMetric 10";
                return new RouteDecision(command, $"preferred the healthy Ethernet interface (gateway {ethPing:0.#} ms) over Wi-Fi (gateway {wifiPing:0.#} ms)");
            }

            return new RouteDecision(null, $"physical routes checked: Ethernet gateway {ethPing:0.#} ms, Wi-Fi gateway {wifiPing:0.#} ms; no metric change justified");
        }
        catch
        {
            return new RouteDecision(null, "route-interface analysis was inconclusive; no interface was modified");
        }
    }

    static async Task<IReadOnlyList<InterfaceInfo>> ReadInterfaces()
    {
        var json = await RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command \"Get-NetIPInterface -AddressFamily IPv4 -ConnectionState Connected | Select-Object ifIndex,InterfaceAlias,InterfaceMetric | ConvertTo-Json -Compress\"", 7000);
        var list = new List<InterfaceInfo>();
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            foreach (var item in items)
            {
                var alias = item.TryGetProperty("InterfaceAlias", out var a) ? a.ToString() : "";
                var index = item.TryGetProperty("ifIndex", out var idx) && idx.TryGetInt32(out var i) ? i : 0;
                var metric = item.TryGetProperty("InterfaceMetric", out var met) && met.TryGetInt32(out var m) ? m : 0;
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.GetIPProperties().GetIPv4Properties()?.Index == index);
                var gateway = nic?.GetIPProperties().GatewayAddresses.Select(x => x.Address).FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "";
                var name = alias + " " + (nic?.Description ?? "");
                var virtualish = Regex.IsMatch(name, "vpn|tap|wintun|wireguard|virtual|hyper-v|vmware|loopback", RegexOptions.IgnoreCase);
                var ethernet = nic?.NetworkInterfaceType == NetworkInterfaceType.Ethernet;
                var wifi = nic?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                list.Add(new InterfaceInfo(index, alias, metric, gateway, ethernet, wifi, virtualish, !string.IsNullOrWhiteSpace(gateway)));
            }
        }
        catch { }
        return list;
    }

    static async Task<double> ProbeGateway(string gateway)
    {
        var samples = await ProbeSeries(gateway, 0, 3);
        var ok = samples.Where(x => x >= 0).ToList();
        return ok.Count == 0 ? -1 : ok.Average();
    }

    static bool IsCrossFire(string name) => name.Contains("crossfire", StringComparison.OrdinalIgnoreCase);

    static bool IsPrivateOrLoopback(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a)) return true;
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    static string? ExtractGateway(Form form)
    {
        try
        {
            var field = form.GetType().GetField("lastNetwork", BindingFlags.Instance | BindingFlags.NonPublic);
            var text = field?.GetValue(form) as string ?? "";
            var m = Regex.Match(text, @"(?im)^GATEWAY\s+(\S+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    static async Task<List<double>> ProbeSeries(string ip, int port, int count)
    {
        var result = new List<double>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add(await Task.Run(() => Probe(ip, port)).ConfigureAwait(false));
            await Task.Delay(90).ConfigureAwait(false);
        }
        return result;
    }

    static double Probe(string ip, int port)
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(ip, 900);
            if (reply.Status == IPStatus.Success) return reply.RoundtripTime;
        }
        catch { }
        if (port > 0 && port < 65536)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var client = new System.Net.Sockets.TcpClient();
                var task = client.ConnectAsync(ip, port);
                if (task.Wait(850) && client.Connected) { sw.Stop(); return sw.Elapsed.TotalMilliseconds; }
            }
            catch { }
        }
        return -1;
    }

    static string ReadValue(string text, string key)
    {
        var m = Regex.Match(text, Regex.Escape(key) + @"\s*:\s*(.+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    static bool NeedsNormalAutotuning(string value) => !string.IsNullOrWhiteSpace(value) && !value.Equals("normal", StringComparison.OrdinalIgnoreCase);
    static bool NeedsEnabled(string value) => !string.IsNullOrWhiteSpace(value) && value.Contains("disabled", StringComparison.OrdinalIgnoreCase);
    static bool WinsockAutotuningDisabled(string text) => Regex.IsMatch(text ?? "", @"(?i)(autotuning\s*:\s*disabled|disabled)");
    static bool SameHealthyBaseline(string text) => !NeedsNormalAutotuning(ReadValue(text, "Receive Window Auto-Tuning Level")) && !NeedsEnabled(ReadValue(text, "Receive-Side Scaling State"));
    static bool LooksSuccessful(string output) => !string.IsNullOrWhiteSpace(output) && !output.Contains("error", StringComparison.OrdinalIgnoreCase) && !output.Contains("denied", StringComparison.OrdinalIgnoreCase) && !output.Contains("failed", StringComparison.OrdinalIgnoreCase);
    static string QuotePs(string command) => "\"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    static async Task<string> RunAsync(string file, string args, int timeoutMs)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };
            p.Start();
            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errorTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch { try { p.Kill(true); } catch { } }
            return (await outputTask.ConfigureAwait(false)) + "\r\n" + (await errorTask.ConfigureAwait(false));
        }
        catch (Exception ex) { return "Command error: " + ex.Message; }
    }

    static void Log(Form form, string message)
    {
        try
        {
            if (form.IsDisposed || !form.IsHandleCreated) return;
            form.BeginInvoke((Action)(() =>
            {
                try
                {
                    var method = form.GetType().GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic);
                    method?.Invoke(form, new object[] { message });
                }
                catch { }
            }));
        }
        catch { }
    }

    sealed record InterfaceInfo(int InterfaceIndex, string Alias, int InterfaceMetric, string Gateway, bool IsEthernet, bool IsWifi, bool IsVirtualOrVpn, bool HasDefaultRoute)
    {
        public bool IsPhysical => IsEthernet || IsWifi;
    }

    sealed record RouteDecision(string? ApplyCommand, string Description);
}
