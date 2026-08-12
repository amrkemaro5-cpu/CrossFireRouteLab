using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Tests the actual verified game target rather than CrossFire's login/master
/// endpoint. For TCP targets it measures fresh TCP connect time. For UDP room
/// targets it uses ICMP as a route-quality proxy because generic UDP has no
/// standard echo/ACK semantics that GRL can safely invent.
///
/// Tests use temporary ActiveStore /32 routes. No persistent route, DNS,
/// firewall, router, or firmware setting is changed.
/// </summary>
internal static class RouteOptimizerPatch
{
    static System.Threading.Timer? timer;
    static bool running;
    static DateTime lastRunUtc = DateTime.MinValue;
    static string target = "";
    static string appliedTarget = "";
    static int appliedInterface;

    static readonly TimeSpan Recheck = TimeSpan.FromMinutes(2);
    const double ImprovementThresholdMs = 3.0;

    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 8000, 5000);
        form.FormClosed += (_, _) =>
        {
            try { timer?.Dispose(); } catch { }
            timer = null;
            if (appliedTarget.Length > 0)
            {
                try { RemoveHostRoute(appliedTarget); } catch { }
            }
            appliedTarget = "";
        };
        Log(form, "[ROUTE AI] Waiting for a verified game/room transport before testing alternate local paths.");
    }

    static void Tick(Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated) return;
        if (DateTime.UtcNow - lastRunUtc < Recheck) return;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();
        var pidObj = type.GetField("gamePid", flags)?.GetValue(form);
        var gameName = type.GetField("gameName", flags)?.GetValue(form) as string ?? "";
        if (pidObj is not int pid || pid <= 0) return;

        string? endpoint = type.GetField("endpoint", flags)?.GetValue(form) as string;
        var portObj = type.GetField("endpointPort", flags)?.GetValue(form);
        string protocol = "TCP";

        if (gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase))
        {
            // Never optimize CrossFire's 10009/13008 master endpoint. Wait
            // until packet discovery has positively identified the game flow.
            if (!CrossFirePacketRoomDiscoveryPatchV2.TryGetRoomTarget(out var roomIp, out var roomPort, out var roomProtocol)) return;
            endpoint = roomIp;
            portObj = roomPort;
            protocol = roomProtocol;
        }
        else
        {
            protocol = "TCP";
        }

        if (string.IsNullOrWhiteSpace(endpoint) || portObj is not int port || port <= 0) return;
        if (!IsPublicIPv4(endpoint)) return;

        var newTarget = endpoint + ":" + port + "/" + protocol;
        if (string.Equals(newTarget, target, StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow - lastRunUtc < Recheck) return;

        target = newTarget;
        lastRunUtc = DateTime.UtcNow;
        running = true;
        _ = Task.Run(() => Optimize(form, pid, gameName, endpoint, port, protocol, newTarget));
    }

    static async Task Optimize(Form form, int pid, string gameName, string endpoint, int port, string protocol, string targetKey)
    {
        try
        {
            Log(form, $"[ROUTE AI] Evaluating real {gameName} transport {endpoint}:{port} {protocol}…");

            var routes = await ReadDefaultRoutes();
            var candidates = routes
                .Where(r => r.Gateway.Length > 0 && r.InterfaceIndex > 0)
                .GroupBy(r => r.InterfaceIndex)
                .Select(g => g.OrderBy(r => r.RouteMetric).First())
                .ToList();

            if (candidates.Count == 0)
            {
                Log(form, "[ROUTE AI] No usable alternate local route was found. Nothing changed.");
                return;
            }

            var virtualCount = candidates.Count(IsVirtual);
            if (virtualCount > 0)
                Log(form, $"[ROUTE AI] {virtualCount} virtual/VPN-capable route(s) detected; they will be benchmarked but never disabled.");

            if (candidates.Count == 1)
            {
                var only = candidates[0];
                Log(form, $"[ROUTE AI] Only one active route is available ({only.Alias} → {only.Gateway}). A local route change cannot manufacture a shorter Internet path.");
                return;
            }

            var baseline = await MeasureTarget(endpoint, port, protocol, 4);
            Log(form, $"[ROUTE AI] Current path baseline: {(baseline < 0 ? "unreachable" : baseline.ToString("0.0") + " ms")} ({protocol}).");

            var results = new List<RouteResult>();
            foreach (var route in candidates)
            {
                if (string.Equals(targetKey, appliedTarget, StringComparison.OrdinalIgnoreCase) && route.InterfaceIndex == appliedInterface)
                    continue;

                if (!TryAddHostRoute(endpoint, route, out var error))
                {
                    Log(form, $"[ROUTE AI] TEST SKIPPED: {route.Alias} ({error}).");
                    continue;
                }

                try
                {
                    var ms = await MeasureTarget(endpoint, port, protocol, 4);
                    results.Add(new RouteResult(route, ms));
                    Log(form, $"[ROUTE AI] TEST {route.Alias} / {route.Gateway}: {(ms < 0 ? "unreachable" : ms.ToString("0.0") + " ms")}.");
                }
                finally
                {
                    RemoveHostRoute(endpoint);
                }
            }

            var valid = results.Where(r => r.LatencyMs >= 0).OrderBy(r => r.LatencyMs).ToList();
            if (valid.Count == 0)
            {
                Log(form, "[ROUTE AI] No alternate path produced a usable measurement. Existing routing was left unchanged.");
                return;
            }

            var best = valid[0];
            if (baseline >= 0 && best.LatencyMs >= baseline - ImprovementThresholdMs)
            {
                Log(form, $"[ROUTE AI] Best alternate path is {best.Route.Alias} at {best.LatencyMs:0.0} ms, not at least {ImprovementThresholdMs:0} ms better. No route applied.");
                return;
            }

            if (!TryAddHostRoute(endpoint, best.Route, out var applyError))
            {
                Log(form, "[ROUTE AI] APPLY FAILED: " + applyError + ". Existing routing remains active.");
                return;
            }

            appliedTarget = targetKey;
            appliedInterface = best.Route.InterfaceIndex;
            Log(form, $"[ROUTE AI] APPLIED: {best.Route.Alias} → {best.Route.Gateway} for {endpoint}:{port} ({protocol}), measured {best.LatencyMs:0.0} ms.");
            Log(form, "[ROUTE AI] This is a session-only /32 ActiveStore route. An already-established CrossFire socket will not migrate until it reconnects.");
            if (protocol == "UDP")
                Log(form, "[ROUTE AI] UDP note: the route decision used ICMP path RTT as a proxy; the in-game CrossFire ping remains the final validation.");
        }
        catch (Exception ex)
        {
            Log(form, "[ROUTE AI] Optimizer stopped safely: " + ex.Message);
        }
        finally
        {
            running = false;
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
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
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

    static bool TryAddHostRoute(string ip, DefaultRoute route, out string error)
    {
        error = "";
        try
        {
            if (GetExactRoute(ip))
            {
                error = "an exact /32 host route already exists";
                return false;
            }

            var command = $"New-NetRoute -DestinationPrefix '{ip}/32' -InterfaceIndex {route.InterfaceIndex} -NextHop '{route.Gateway}' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null";
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

    static bool GetExactRoute(string ip)
    {
        var command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Select-Object -First 1 | ConvertTo-Json -Compress";
        var output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 5000);
        return !string.IsNullOrWhiteSpace(output) && !output.Trim().Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    static void RemoveHostRoute(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        var command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{ $_.RouteMetric -eq 1 }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";
        _ = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 6000);
    }

    static async Task<double> MeasureTarget(string ip, int port, string protocol, int count)
    {
        return protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase)
            ? await MeasureIcmp(ip, count).ConfigureAwait(false)
            : await MeasureTcp(ip, port, count).ConfigureAwait(false);
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
            await Task.Delay(100).ConfigureAwait(false);
        }
        return values.Count == 0 ? -1 : values.OrderBy(x => x).ElementAt(values.Count / 2);
    }

    static async Task<double> MeasureIcmp(string ip, int count)
    {
        var values = new List<double>();
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var reply = await ping.SendPingAsync(ip, 1200).ConfigureAwait(false);
                sw.Stop();
                if (reply.Status == IPStatus.Success) values.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch { }
            await Task.Delay(100).ConfigureAwait(false);
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
        => !output.Contains("Exception", StringComparison.OrdinalIgnoreCase) && !output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) && !output.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);

    static string Run(string file, string args, int timeoutMs)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        p.Start();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return "timeout"; }
        return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
    }

    static async Task<string> RunAsync(string file, string args, int timeoutMs)
        => await Task.Run(() => Run(file, args, timeoutMs)).ConfigureAwait(false);

    static void Log(Form form, string text)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try { form.BeginInvoke((Action)(() => form.GetType().GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }

    readonly record struct DefaultRoute(int InterfaceIndex, string Alias, string Description, string Gateway, int RouteMetric);
    readonly record struct RouteResult(DefaultRoute Route, double LatencyMs);
}
