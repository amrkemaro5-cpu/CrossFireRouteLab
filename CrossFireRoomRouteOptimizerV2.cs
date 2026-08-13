using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

namespace CrossFireRouteLab;

/// <summary>
/// CrossFire TCP-only route optimizer.
/// Every route benchmark opens a fresh TCP connection to the exact live
/// CrossFire TCP endpoint supplied by CrossFireRoomTransportProbeV3.
/// No ICMP/UDP proxy is used.
/// </summary>
internal static class CrossFireRoomRouteOptimizerV2
{
    static System.Threading.Timer? timer;
    static bool running;
    static DateTime lastRun = DateTime.MinValue;
    static string lastTarget = "";
    static string appliedTarget = "";
    const double ImprovementMs = 3.0;

    public static void Apply(GameRouteLabV10Form form)
    {
        StopOldOptimizer();
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 12000, 8000);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; if (appliedTarget.Length > 0) RemoveHostRoute(appliedTarget); };
        Log(form, "[ROUTE AI TCP] Waiting for the live CrossFire TCP endpoint before benchmarking routes.");
    }

    static void StopOldOptimizer()
    {
        try
        {
            var type = typeof(Program).Assembly.GetType("CrossFireRouteLab.RouteOptimizerPatch");
            var field = type?.GetField("timer", BindingFlags.Static | BindingFlags.NonPublic);
            if (field?.GetValue(null) is IDisposable d) d.Dispose();
            field?.SetValue(null, null);
        }
        catch { }
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated || DateTime.UtcNow - lastRun < TimeSpan.FromMinutes(2)) return;
        if (!CrossFireRoomTransportProbeV3.TryGetTarget(out var ip, out var port, out var protocol)) return;
        if (!protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)) return;
        var key = $"{ip}:{port}/TCP";
        if (key.Equals(lastTarget, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - lastRun < TimeSpan.FromMinutes(2)) return;
        lastTarget = key;
        lastRun = DateTime.UtcNow;
        running = true;
        _ = Task.Run(() => Optimize(form, ip, port, "TCP"));
    }

    static async Task Optimize(GameRouteLabV10Form form, string ip, int port, string protocol)
    {
        try
        {
            if (!protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)) return;
            Log(form, $"[ROUTE AI TCP] Benchmarking actual CrossFire TCP endpoint {ip}:{port}; no UDP/ICMP proxy.");
            var routes = await ReadDefaultRoutes();
            var candidates = routes.GroupBy(x => x.InterfaceIndex).Select(g => g.OrderBy(x => x.RouteMetric).First()).Where(x => x.Gateway.Length > 0 && x.InterfaceIndex > 0).ToList();
            if (candidates.Count <= 1)
            {
                Log(form, candidates.Count == 1 ? $"[ROUTE AI TCP] Only one active default route is available: {candidates[0].Alias} → {candidates[0].Gateway}." : "[ROUTE AI TCP] No alternate default route is available.");
                return;
            }

            var baseline = await MeasureTcp(ip, port, 4);
            Log(form, $"[ROUTE AI TCP] Baseline TCP connect RTT: {(baseline < 0 ? "unreachable" : baseline.ToString("0.0") + " ms")}.");
            var results = new List<(DefaultRoute Route, double Ms)>();
            foreach (var route in candidates)
            {
                if (!TryAddHostRoute(ip, route, out var error)) { Log(form, $"[ROUTE AI TCP] TEST SKIPPED {route.Alias}: {error}"); continue; }
                try
                {
                    var ms = await MeasureTcp(ip, port, 4);
                    results.Add((route, ms));
                    Log(form, $"[ROUTE AI TCP] {route.Alias} / {route.Gateway}: {(ms < 0 ? "unreachable" : ms.ToString("0.0") + " ms")}.");
                }
                finally { RemoveHostRoute(ip); }
            }

            var valid = results.Where(x => x.Ms >= 0).OrderBy(x => x.Ms).ToList();
            if (valid.Count == 0) { Log(form, "[ROUTE AI TCP] No alternate route produced a usable TCP measurement; routing unchanged."); return; }
            var best = valid[0];
            if (baseline >= 0 && best.Ms >= baseline - ImprovementMs)
            {
                Log(form, $"[ROUTE AI TCP] No route was at least {ImprovementMs:0} ms better than the baseline TCP RTT. Nothing applied.");
                return;
            }
            if (!TryAddHostRoute(ip, best.Route, out var applyError)) { Log(form, "[ROUTE AI TCP] APPLY FAILED: " + applyError); return; }
            appliedTarget = ip;
            Log(form, $"[ROUTE AI TCP] APPLIED {best.Route.Alias} → {best.Route.Gateway} for {ip}/32, measured {best.Ms:0.0} ms via TCP connect.");
            Log(form, "[ROUTE AI TCP] ActiveStore /32 only; Windows drops it on reboot. Reconnect the CrossFire TCP socket to validate the new path.");
        }
        catch (Exception ex) { Log(form, "[ROUTE AI TCP] Stopped safely: " + ex.Message); }
        finally { running = false; }
    }

    static async Task<double> MeasureTcp(string ip, int port, int count)
    {
        var values = new List<double>();
        for (int i = 0; i < count; i++)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                var sw = Stopwatch.StartNew();
                var task = client.ConnectAsync(ip, port);
                if (await Task.WhenAny(task, Task.Delay(1400)).ConfigureAwait(false) == task && client.Connected)
                {
                    sw.Stop();
                    values.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            catch { }
            await Task.Delay(120).ConfigureAwait(false);
        }
        return values.Count == 0 ? -1 : values.OrderBy(x => x).ElementAt(values.Count / 2);
    }

    static async Task<List<DefaultRoute>> ReadDefaultRoutes()
    {
        const string command = "Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore ActiveStore | ForEach-Object { $a=Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue; [pscustomobject]@{InterfaceIndex=$_.InterfaceIndex;Alias=$a.Name;Description=$a.InterfaceDescription;Status=$a.Status;Gateway=$_.NextHop;RouteMetric=$_.RouteMetric} } | ConvertTo-Json -Compress";
        var json = await RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 8000);
        var list = new List<DefaultRoute>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
            foreach (var x in items)
            {
                int idx = ReadInt(x, "InterfaceIndex"), metric = ReadInt(x, "RouteMetric");
                string alias = ReadString(x, "Alias"), desc = ReadString(x, "Description"), status = ReadString(x, "Status"), gateway = ReadString(x, "Gateway");
                if (idx > 0 && gateway.Length > 0 && status.Equals("Up", StringComparison.OrdinalIgnoreCase)) list.Add(new DefaultRoute(idx, alias, desc, gateway, metric));
            }
        }
        catch { }
        return list;
    }

    static bool TryAddHostRoute(string ip, DefaultRoute route, out string error)
    {
        error = "";
        try
        {
            if (GetExactRoute(ip)) { error = "an exact /32 host route already exists"; return false; }
            string command = $"New-NetRoute -DestinationPrefix '{ip}/32' -InterfaceIndex {route.InterfaceIndex} -NextHop '{route.Gateway}' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null";
            string output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 8000);
            if (output.Contains("Exception", StringComparison.OrdinalIgnoreCase) || output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)) { error = "Windows rejected the temporary route"; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    static bool GetExactRoute(string ip)
    {
        string command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Select-Object -First 1 | ConvertTo-Json -Compress";
        string output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 5000);
        return !string.IsNullOrWhiteSpace(output) && !output.Trim().Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    static void RemoveHostRoute(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        string command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{ $_.RouteMetric -eq 1 }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";
        _ = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 6000);
    }

    static string ReadString(JsonElement x, string name) => x.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";
    static int ReadInt(JsonElement x, string name) => x.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : 0;
    static string QuotePs(string text) => "'" + text.Replace("'", "''") + "'";
    static string Run(string file, string args, int timeoutMs)
    {
        using var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        p.Start();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "timeout"; }
        return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    }
    static async Task<string> RunAsync(string file, string args, int timeoutMs) => await Task.Run(() => Run(file, args, timeoutMs)).ConfigureAwait(false);
    static void Log(GameRouteLabV10Form form, string text) { try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { } }
    readonly record struct DefaultRoute(int InterfaceIndex, string Alias, string Description, string Gateway, int RouteMetric);
}
