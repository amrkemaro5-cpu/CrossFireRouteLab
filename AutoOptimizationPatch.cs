using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Final-stage diagnostic/optimization layer for the v10 dashboard.
/// It intentionally leaves the v10 layout untouched. After a real endpoint has
/// been selected and enough samples exist, it diagnoses the local link versus
/// the remote path and applies only conservative, reversible host-side fixes.
/// It never changes DNS, Windows routes, VPN/TAP adapters, firewall rules, or
/// router settings automatically.
/// </summary>
internal static class AutoOptimizationPatch
{
    static readonly TimeSpan Warmup = TimeSpan.FromSeconds(12);
    static readonly TimeSpan Recheck = TimeSpan.FromMinutes(5);
    static DateTime _startedUtc;
    static DateTime _lastRunUtc = DateTime.MinValue;
    static bool _running;
    static System.Threading.Timer? _timer;

    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        _startedUtc = DateTime.UtcNow;
        _timer = new System.Threading.Timer(_ => Tick(form), null, 4000, 4000);
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

        if (pidValue is not int pid || pid <= 0 || string.IsNullOrWhiteSpace(endpoint) || pingHistory == null || pingHistory.Count < 8)
            return;

        if (!IsPrivateOrLoopback(endpoint))
        {
            _running = true;
            _lastRunUtc = DateTime.UtcNow;
            _ = Task.Run(() => DiagnoseAndOptimize(form, pid, gameName, endpoint!, portValue is int p ? p : 0));
        }
    }

    static async Task DiagnoseAndOptimize(Form form, int pid, string gameName, string endpoint, int port)
    {
        try
        {
            Log(form, "[AI] Diagnosis complete enough to evaluate the live route. Starting safe optimization pass…");

            var tcpState = await RunAsync("netsh.exe", "interface tcp show global", 5000);
            var endpointSamples = await ProbeSeries(endpoint, port, 8);
            var endpointOk = endpointSamples.Where(x => x >= 0).ToList();
            var loss = endpointSamples.Count == 0 ? 1.0 : 1.0 - (double)endpointOk.Count / endpointSamples.Count;
            var avg = endpointOk.Count == 0 ? -1 : endpointOk.Average();
            var min = endpointOk.Count == 0 ? -1 : endpointOk.Min();
            var max = endpointOk.Count == 0 ? -1 : endpointOk.Max();
            var spread = endpointOk.Count == 0 ? -1 : max - min;

            var gateway = ExtractGateway(form);
            var gatewaySamples = string.IsNullOrWhiteSpace(gateway) ? new List<double>() : await ProbeSeries(gateway!, 0, 4);
            var gatewayOk = gatewaySamples.Where(x => x >= 0).ToList();
            var gatewayAvg = gatewayOk.Count == 0 ? -1 : gatewayOk.Average();

            var diagnosis = Classify(avg, loss, spread, gatewayAvg, endpointSamples.Count, endpointOk.Count);
            Log(form, $"[AI] {diagnosis}");
            Log(form, $"[AI] Endpoint {endpoint}:{port} | avg {(avg < 0 ? "—" : avg.ToString("0"))} ms | loss {(loss * 100):0.#}% | spread {(spread < 0 ? "—" : spread.ToString("0"))} ms.");
            if (gatewayAvg >= 0)
                Log(form, $"[AI] Gateway {gateway} | avg {gatewayAvg:0.#} ms. This separates local-link delay from Internet/server-path delay.");

            var applied = new List<string>();
            var skipped = new List<string>();

            // Conservative Windows TCP baseline. Microsoft documents Normal as the
            // default receive-window autotuning level; RSS is a standard receive-side
            // scaling feature. Only correct these settings when they are visibly
            // non-default/degraded, and only if Windows permits the change.
            var normalized = tcpState.ToLowerInvariant();
            var autotune = ReadValue(tcpState, "Receive Window Auto-Tuning Level");
            var rss = ReadValue(tcpState, "Receive-Side Scaling State");
            if (NeedsNormalAutotuning(autotune) || NeedsEnabled(rss))
            {
                var args = "interface tcp set global autotuninglevel=normal rss=enabled";
                var change = await RunAsync("netsh.exe", args, 7000);
                if (LooksSuccessful(change))
                {
                    applied.Add("restored Windows TCP autotuning to Normal and RSS to Enabled");
                    Log(form, "[AI] APPLIED: " + applied[^1] + ".");
                }
                else
                {
                    skipped.Add("TCP baseline correction (Windows requires administrator permission)");
                    Log(form, "[AI] SKIPPED: TCP baseline correction could not be applied. No other system setting was changed.");
                }
            }
            else
            {
                Log(form, "[AI] TCP baseline already looks healthy; no global TCP change needed.");
            }

            // AboveNormal is deliberately the ceiling for the game process. High and
            // Realtime are never used because Microsoft warns that high/realtime can
            // starve other system work. This is only applied to a detected game and
            // is naturally lost when that process exits.
            if (IsCrossFire(gameName))
            {
                try
                {
                    using var game = Process.GetProcessById(pid);
                    if (game.PriorityClass is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal or ProcessPriorityClass.Normal)
                    {
                        game.PriorityClass = ProcessPriorityClass.AboveNormal;
                        applied.Add("set CrossFire process priority to AboveNormal");
                        Log(form, "[AI] APPLIED: CrossFire process priority → AboveNormal.");
                    }
                    else
                    {
                        Log(form, $"[AI] CrossFire process priority already {game.PriorityClass}; no change needed.");
                    }
                }
                catch (Exception ex)
                {
                    skipped.Add("CrossFire process priority (access denied)");
                    Log(form, "[AI] SKIPPED: CrossFire priority could not be changed: " + ex.Message);
                }
            }

            if (gatewayAvg >= 0 && avg >= 0 && gatewayAvg <= 5 && avg >= gatewayAvg + 50)
            {
                Log(form, "[AI] IMPORTANT: local gateway is healthy while the game endpoint is much farther away. The dominant latency is upstream/server-path distance or peering; changing DNS or the local route blindly would not be justified.");
            }
            else if (gatewayAvg > 5)
            {
                Log(form, "[AI] IMPORTANT: gateway latency is elevated. Investigate the local Ethernet/Wi-Fi link, router load, or competing traffic before changing Internet routes.");
            }

            if (loss >= 0.10)
                Log(form, "[AI] IMPORTANT: probe loss is elevated. The app will not label this as game-packet loss without packet-level evidence.");
            else if (avg >= 0 && avg < 80 && spread < 15)
                Log(form, "[AI] RESULT: route is low-latency and stable; no aggressive optimization is justified.");
            else if (avg >= 0 && spread < 15)
                Log(form, "[AI] RESULT: route is stable but latency is high; this is more consistent with path/server distance than local jitter.");

            Log(form, applied.Count == 0
                ? "[AI] No automatic changes were necessary."
                : "[AI] Automatic changes applied: " + string.Join("; ", applied) + ".");
            if (skipped.Count > 0)
                Log(form, "[AI] Not applied: " + string.Join("; ", skipped) + ".");
            Log(form, "[AI] Safety boundary: DNS, Windows route table, VPN/TAP adapters, firewall and router settings were NOT changed automatically.");
        }
        catch (Exception ex)
        {
            Log(form, "[AI] Optimization pass failed safely: " + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    static string Classify(double avg, double loss, double spread, double gatewayAvg, int samples, int successful)
    {
        if (successful == 0) return "No successful endpoint probes; diagnosis remains observational only.";
        if (loss >= .10) return "Endpoint quality is degraded by elevated probe loss; avoid route changes until the loss source is confirmed.";
        if (gatewayAvg > 5) return "Local-link warning: the gateway itself is adding measurable delay.";
        if (avg >= 0 && gatewayAvg >= 0 && avg >= gatewayAvg + 50) return "Stable local link, but substantial delay is being added beyond the gateway.";
        if (spread >= 30) return "Latency is variable; jitter/spread is large enough to warrant continued observation.";
        return "Stable endpoint path; only conservative host-side corrections are eligible.";
    }

    static bool IsCrossFire(string name) => name.Contains("crossfire", StringComparison.OrdinalIgnoreCase);

    static bool IsPrivateOrLoopback(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var a)) return true;
        if (System.Net.IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254);
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
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = ping.Send(ip, 900);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success) return reply.RoundtripTime;
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
    static bool LooksSuccessful(string output) => !string.IsNullOrWhiteSpace(output) && !output.Contains("error", StringComparison.OrdinalIgnoreCase) && !output.Contains("denied", StringComparison.OrdinalIgnoreCase);

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
}
