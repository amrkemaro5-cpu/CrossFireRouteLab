using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Session route optimizer.
///
/// GRL previously ranked only the endpoints CrossFire was already using. That
/// can select a measurement target, but it cannot improve the path. This layer
/// tests every usable local default route by installing a temporary /32 route
/// for the actual game endpoint in Windows ActiveStore, measuring a fresh TCP
/// connection, removing the test route, and finally keeping the fastest route
/// for the session when it is meaningfully better.
///
/// Important limits:
/// - It never invents an ISP path. With one physical WE route, there is nothing
///   else on the PC to switch to.
/// - Existing VPN/TAP/Wintun routes are never disabled or rewritten.
/// - Routes are ActiveStore-only (not persistent) and are removed when the
///   endpoint changes or the application closes.
/// - The game must reconnect before an already-established socket can use a new
///   route. GRL never kills or restarts CrossFire automatically.
/// </summary>
internal static class RouteOptimizerPatch
{
    static System.Threading.Timer? _timer;
    static bool _running;
    static DateTime _lastRunUtc = DateTime.MinValue;
    static string _target = "";
    static string _appliedTarget = "";
    static int _appliedInterface;
    static string _appliedGateway = "";

    static readonly TimeSpan Recheck = TimeSpan.FromMinutes(2);
    const double ImprovementThresholdMs = 3.0;

    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        _timer = new System.Threading.Timer(_ => Tick(form), null, 7000, 5000);
        form.FormClosed += (_, _) =>
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;
            if (_appliedTarget.Length > 0)
            {
                try { RemoveHostRoute(_appliedTarget); } catch { }
            }
            _appliedTarget = "";
        };
        Log(form, "[ROUTE AI] Active route optimizer enabled: testing every usable local path before applying a better /32 session route.");
    }

    static void Tick(Form form)
    {
        if (_running || form.IsDisposed || !form.IsHandleCreated) return;
        if (DateTime.UtcNow - _lastRunUtc < Recheck) return;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();
        var pidObj = type.GetField("gamePid", flags)?.GetValue(form);
        var endpoint = type.GetField("endpoint", flags)?.GetValue(form) as string;
        var portObj = type.GetField("endpointPort", flags)?.GetValue(form);
        var gameName = type.GetField("gameName", flags)?.GetValue(form) as string ?? "";

        if (pidObj is not int pid || pid <= 0 || string.IsNullOrWhiteSpace(endpoint) || portObj is not int port || port <= 0)
            return;
        if (!IsPublicIPv4(endpoint)) return;

        var target = endpoint + ":" + port;
        if (string.Equals(target, _target, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - _lastRunUtc < Recheck)
            return;

        _target = target;
        _lastRunUtc = DateTime.UtcNow;
        _running = true;
        _ = Task.Run(() => Optimize(form, pid, gameName, endpoint, port, target));
    }

    static async Task Optimize(Form form, int pid, string gameName, string endpoint, int port, string target)
    {
        try
        {
            Log(form, $"[ROUTE AI] Evaluating routes for {gameName} → {target}…");

            var routes = await ReadDefaultRoutes();
            if (routes.Count == 0)
            {
                Log(form, "[ROUTE AI] No usable IPv4 default route was found. Nothing changed.");
                return;
            }

            var virtualRoutes = routes.Where(IsVirtual).ToList();
            var physicalRoutes = routes.Where(r => !IsVirtual(r)).ToList();
            if (virtualRoutes.Count > 0)
                Log(form, "[ROUTE AI] VPN/TAP/Wintun routes detected; they remain untouched and are not disabled automatically.");

            var candidates = physicalRoutes
                .Where(r => r.Gateway.Length > 0 && r.InterfaceIndex > 0)
                .GroupBy(r => r.InterfaceIndex)
                .Select(g => g.OrderBy(r => r.RouteMetric).First())
                .ToList();

            if (candidates.Count <= 1)
            {
                if (candidates.Count == 0)
                {
                    Log(form, "[ROUTE AI] No physical alternate path exists on this PC. WE's current route is the only route GRL can safely use.");
                }
                else
                {
                    var only = candidates[0];
                    Log(form, $"[ROUTE AI] Only one physical path is available ({only.Alias} → {only.Gateway}). A PC-side route change cannot manufacture a shorter WE/Internet path.");
                }
                return;
            }

            var baseline = await MeasureTcp(endpoint, port, 3);
            Log(form, $"[ROUTE AI] Baseline/current route: {(baseline < 0 ? "unreachable" : baseline.ToString("0.0") + " ms")}.");

            var results = new List<RouteResult>();
            foreach (var route in candidates)
            {
                if (string.Equals(target, _appliedTarget, StringComparison.OrdinalIgnoreCase) && route.InterfaceIndex == _appliedInterface)
                    continue;

                if (!TryAddHostRoute(target, route, out var error))
                {
                    Log(form, $"[ROUTE AI] TEST SKIPPED: {route.Alias} ({error}).");
                    continue;
                }

                try
                {
                    var ms = await MeasureTcp(endpoint, port, 3);
                    results.Add(new RouteResult(route, ms));
                    Log(form, $"[ROUTE AI] TEST {route.Alias} / gateway {route.Gateway}: {(ms < 0 ? "unreachable" : ms.ToString("0.0") + " ms")}.");
                }
                finally
                {
                    RemoveHostRoute(target);
                }
            }

            if (results.Count == 0)
            {
                Log(form, "[ROUTE AI] No alternate route could be tested. Existing Windows routing was left unchanged.");
                return;
            }

            var valid = results.Where(r => r.LatencyMs >= 0).OrderBy(r => r.LatencyMs).ToList();
            if (valid.Count == 0)
            {
                Log(form, "[ROUTE AI] All alternate route probes failed. Existing Windows routing was left unchanged.");
                return;
            }

            var best = valid[0];
            var currentBest = baseline;
            if (currentBest >= 0 && best.LatencyMs >= currentBest - ImprovementThresholdMs)
            {
                Log(form, $"[ROUTE AI] Best tested route is {best.Route.Alias} at {best.LatencyMs:0.0} ms, but it is not at least {ImprovementThresholdMs:0} ms better than the current route. No change applied.");
                return;
            }

            if (!TryAddHostRoute(target, best.Route, out var applyError))
            {
                Log(form, "[ROUTE AI] APPLY FAILED: " + applyError + ". Existing routing remains active.");
                return;
            }

            _appliedTarget = target;
            _appliedInterface = best.Route.InterfaceIndex;
            _appliedGateway = best.Route.Gateway;
            Log(form, $"[ROUTE AI] APPLIED: {best.Route.Alias} → {best.Route.Gateway} is now the active session route for {target} ({best.LatencyMs:0.0} ms measured).");
            Log(form, "[ROUTE AI] IMPORTANT: this affects new connections. CrossFire's already-established match socket will not move until the game reconnects.");
            Log(form, "[ROUTE AI] SAFETY: session-only /32 route; no persistent route, DNS, VPN, firewall, router or firmware change was made.");
        }
        catch (Exception ex)
        {
            Log(form, "[ROUTE AI] Optimizer stopped safely: " + ex.Message);
        }
        finally
        {
            _running = false;
        }
    }

    static async Task<List<DefaultRoute>> ReadDefaultRoutes()
    {
        const string command = "Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore ActiveStore | ForEach-Object { $a=Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue; [pscustomobject]@{InterfaceIndex=$_.InterfaceIndex;Alias=$a.Name;Description=$a.InterfaceDescription;Status=$a.Status;Gateway=$_.NextHop;RouteMetric=$_.RouteMetric} } | ConvertTo-Json -Compress";
        var json = await RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 8000);
        var result = new List<DefaultRoute>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            foreach (var item in items)
            {
                var idx = ReadInt(item, "InterfaceIndex");
                var metric = ReadInt(item, "RouteMetric");
                var alias = ReadString(item, "Alias");
                var desc = ReadString(item, "Description");
                var status = ReadString(item, "Status");
                var gateway = ReadString(item, "Gateway");
                if (idx > 0 && gateway.Length > 0 && status.Equals("Up", StringComparison.OrdinalIgnoreCase))
                    result.Add(new DefaultRoute(idx, alias, desc, gateway, metric));
            }
        }
        catch { }
        return result;
    }

    static bool TryAddHostRoute(string target, DefaultRoute route, out string error)
    {
        error = "";
        try
        {
            var existing = GetExactRoute(target);
            if (existing)
            {
                error = "an exact host route already exists";
                return false;
            }

            var command = $"New-NetRoute -DestinationPrefix '{target.Split(':')[0]}/32' -InterfaceIndex {route.InterfaceIndex} -NextHop '{route.Gateway}' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null";
            var output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 8000);
            if (!LooksSuccessful(output))
            {
                error = "Windows rejected the temporary route";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    static bool GetExactRoute(string target)
    {
        var ip = target.Split(':')[0];
        var command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Select-Object -First 1 | ConvertTo-Json -Compress";
        var output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 5000);
        return !string.IsNullOrWhiteSpace(output) && !output.Trim().Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    static void RemoveHostRoute(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        var ip = target.Split(':')[0];
        var command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{ $_.RouteMetric -eq 1 }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";
        _ = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 6000);
    }

    static async Task<double> MeasureTcp(string ip, int port, int count)
    {
        var values = new List<double>();
        for (var i = 0; i < count; i++)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                var sw = Stopwatch.StartNew();
                var task = client.ConnectAsync(ip, port);
                if (await Task.WhenAny(task, Task.Delay(1200)).ConfigureAwait(false) == task && client.Connected)
                {
                    sw.Stop();
                    values.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
            catch { }
            await Task.Delay(80).ConfigureAwait(false);
        }
        return values.Count == 0 ? -1 : values.OrderBy(x => x).ElementAt(values.Count / 2);
    }

    static bool IsVirtual(DefaultRoute route)
    {
        var s = (route.Alias + " " + route.Description).ToLowerInvariant();
        return Regex.IsMatch(s, @"vpn|tap|wintun|wireguard|openvpn|zerotier|tailscale|hamachi|virtual|hyper-v|vmware|virtualbox|wan miniport");
    }

    static bool IsPublicIPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || (b[0] == 169 && b[1] == 254) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
    }

    static string ReadString(JsonElement item, string name)
        => item.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";

    static int ReadInt(JsonElement item, string name)
        => item.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : 0;

    static string QuotePs(string text) => "'" + text.Replace("'", "''") + "'";

    static bool LooksSuccessful(string output)
        => !output.Contains("Exception", StringComparison.OrdinalIgnoreCase) &&
           !output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) &&
           !output.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);

    static string Run(string file, string args, int timeoutMs)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        p.Start();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "timeout"; }
        return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    }

    static async Task<string> RunAsync(string file, string args, int timeoutMs)
        => await Task.Run(() => Run(file, args, timeoutMs)).ConfigureAwait(false);

    static void Log(Form form, string text)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try
        {
            form.BeginInvoke((Action)(() => form.GetType().GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text })));
        }
        catch { }
    }

    readonly record struct DefaultRoute(int InterfaceIndex, string Alias, string Description, string Gateway, int RouteMetric);
    readonly record struct RouteResult(DefaultRoute Route, double LatencyMs);
}
