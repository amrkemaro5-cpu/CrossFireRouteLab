using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

namespace CrossFireRouteLab;

/// <summary>
/// Unified CrossFire target discovery and route optimization coordinator.
/// Discovery observes the CrossFire process family and keeps live TCP candidates.
/// Route testing is separate and never claims a TCP probe is the game's displayed ping.
/// </summary>
internal static class CrossFireRouteEngine
{
    static System.Threading.Timer? timer;
    static bool running;
    static readonly Dictionary<string, TcpTarget> history = new(StringComparer.OrdinalIgnoreCase);
    static string targetKey = "";
    static DateTime lastOptimize = DateTime.MinValue;
    static DateTime lastPublish = DateTime.MinValue;
    static string appliedTarget = "";
    static readonly HashSet<int> ControlPorts = new() { 10009, 13008, 16666, 9110 };
    static readonly HashSet<int> NoisePorts = new() { 53, 67, 68, 123, 1900, 3702, 5353, 5222, 3478, 5349, 80, 443, 8080, 8443 };

    public static void Apply(GameRouteLabV10Form form)
    {
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 500, 1500);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; if (!string.IsNullOrWhiteSpace(appliedTarget)) RemoveOwnedRoute(appliedTarget); };
        Log(form, "[CROSSFIRE] Unified target/route engine armed. Discovery and route measurement are separate.");
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated) return;
        var process = FindCrossFireProcess();
        if (process == null) return;
        try { StopGenericTimers(form); SetField(form, "gamePid", process.Id); SetField(form, "gameName", process.ProcessName); }
        finally { process.Dispose(); }
        running = true;
        _ = Task.Run(() => ScanAndPublish(form));
    }

    static async Task ScanAndPublish(GameRouteLabV10Form form)
    {
        try
        {
            var pids = GetCrossFireProcessFamily();
            var current = ReadTcpConnections(pids);
            var now = DateTime.UtcNow;
            foreach (var c in current)
            {
                var key = $"{c.Ip}:{c.Port}";
                if (history.TryGetValue(key, out var old)) history[key] = old with { LastSeen = now, State = c.State, SeenCount = old.SeenCount + 1, OwnerPid = c.OwnerPid };
                else history[key] = new TcpTarget(c.Ip, c.Port, c.State, now, now, 1, c.OwnerPid);
            }
            foreach (var key in history.Where(x => now - x.Value.LastSeen > TimeSpan.FromSeconds(20)).Select(x => x.Key).ToList()) history.Remove(key);

            var candidates = history.Values
                .Where(x => x.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
                .Where(x => IsPublicIPv4(x.Ip) && x.Port > 0 && !NoisePorts.Contains(x.Port))
                .OrderByDescending(x => !ControlPorts.Contains(x.Port))
                .ThenByDescending(x => x.SeenCount)
                .ThenByDescending(x => x.LastSeen)
                .Take(20).ToList();

            if (candidates.Count == 0) { PublishWaiting(form); return; }

            // Discovery never opens a new connection to decide which socket is the room.
            // A persistent non-control TCP socket wins; control ports are fallback only.
            var best = candidates.FirstOrDefault(x => !ControlPorts.Contains(x.Port));
            if (best.Equals(default(TcpTarget))) best = candidates[0];
            var keyTarget = $"{best.Ip}:{best.Port}";
            var changed = !string.Equals(targetKey, keyTarget, StringComparison.OrdinalIgnoreCase);
            targetKey = keyTarget;
            Publish(form, candidates, best, changed);

            if (changed || DateTime.UtcNow - lastOptimize >= TimeSpan.FromMinutes(3))
            {
                lastOptimize = DateTime.UtcNow;
                await Optimize(form, best.Ip, best.Port).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Log(form, "[CROSSFIRE] Engine error: " + ex.Message); }
        finally { running = false; }
    }

    static HashSet<int> GetCrossFireProcessFamily()
    {
        var result = new HashSet<int>();
        string? mainDirectory = null;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(p.Id);
                try { mainDirectory ??= Path.GetDirectoryName(p.MainModule?.FileName); } catch { }
            }
            catch { }
            finally { p.Dispose(); }
        }
        if (!string.IsNullOrWhiteSpace(mainDirectory))
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (!string.IsNullOrWhiteSpace(path) && string.Equals(Path.GetDirectoryName(path), mainDirectory, StringComparison.OrdinalIgnoreCase)) result.Add(p.Id);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return result;
    }

    static Process? FindCrossFireProcess()
    {
        try { foreach (var p in Process.GetProcesses()) { try { if (p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return p; } catch { p.Dispose(); } } } catch { }
        return null;
    }

    static List<TcpCandidate> ReadTcpConnections(HashSet<int> pids)
    {
        var text = Run("netstat.exe", "-n -o -p tcp", 2500);
        var result = new List<TcpCandidate>();
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith("TCP ", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !int.TryParse(parts[^1], out var ownerPid) || !pids.Contains(ownerPid)) continue;
            var state = parts[^2];
            if (!state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) && !state.Equals("SYN_SENT", StringComparison.OrdinalIgnoreCase) && !state.Equals("SYN_RECEIVED", StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryEndpoint(parts[2], out var ip, out var port)) continue;
            if (!IsPublicIPv4(ip) || port <= 0 || NoisePorts.Contains(port)) continue;
            result.Add(new TcpCandidate(ip, port, state, ownerPid));
        }
        return result.GroupBy(x => $"{x.Ip}:{x.Port}", StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
    }

    static async Task Optimize(GameRouteLabV10Form form, string ip, int port)
    {
        try
        {
            Log(form, $"[ROUTE AI] Target {ip}:{port}. Testing available Windows routes with TCP probes. This is a route metric, not the CrossFire room-ping display.");
            var routes = await ReadDefaultRoutes().ConfigureAwait(false);
            var candidates = routes.GroupBy(x => x.InterfaceIndex).Select(g => g.OrderBy(x => x.RouteMetric).First()).Where(x => x.Gateway.Length > 0 && x.InterfaceIndex > 0 && x.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count <= 1) { Log(form, candidates.Count == 1 ? $"[ROUTE AI] One usable default path: {candidates[0].Alias} → {candidates[0].Gateway}. No alternate path to compare." : "[ROUTE AI] No usable default paths were found."); return; }

            var baseline = await MeasureTcp(ip, port, 5).ConfigureAwait(false);
            Log(form, $"[ROUTE AI] Baseline TCP route metric: {FormatMs(baseline)}.");
            var results = new List<(DefaultRoute Route, double Ms)>();
            foreach (var route in candidates)
            {
                if (!TryInstallOwnedRoute(ip, route, out var error)) { Log(form, $"[ROUTE AI] {route.Alias}: test skipped — {error}"); continue; }
                try { var ms = await MeasureTcp(ip, port, 5).ConfigureAwait(false); results.Add((route, ms)); Log(form, $"[ROUTE AI] {route.Alias} / {route.Gateway}: {FormatMs(ms)}."); }
                finally { RemoveOwnedRoute(ip); }
            }
            var valid = results.Where(x => x.Ms >= 0).OrderBy(x => x.Ms).ToList();
            if (valid.Count == 0) { Log(form, "[ROUTE AI] No alternate path produced a usable TCP result. Routing unchanged."); return; }
            var best = valid[0];
            if (baseline >= 0 && best.Ms >= baseline - 3.0) { Log(form, "[ROUTE AI] No alternate path improved the baseline by at least 3 ms. Nothing applied."); return; }
            if (!TryInstallOwnedRoute(ip, best.Route, out var applyError)) { Log(form, "[ROUTE AI] Apply failed: " + applyError); return; }
            appliedTarget = ip;
            Log(form, $"[ROUTE AI] APPLIED {best.Route.Alias} → {best.Route.Gateway} for {ip}/32; route probe {best.Ms:0.0} ms.");
            Log(form, "[ROUTE AI] Temporary ActiveStore /32 route. A new game connection is required to validate the path.");
        }
        catch (Exception ex) { Log(form, "[ROUTE AI] Stopped safely: " + ex.Message); }
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
                if (await Task.WhenAny(task, Task.Delay(1400)).ConfigureAwait(false) == task && client.Connected) { sw.Stop(); values.Add(sw.Elapsed.TotalMilliseconds); }
            }
            catch { }
            await Task.Delay(100).ConfigureAwait(false);
        }
        return values.Count == 0 ? -1 : values.OrderBy(x => x).ElementAt(values.Count / 2);
    }

    static async Task<List<DefaultRoute>> ReadDefaultRoutes()
    {
        const string command = "Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore ActiveStore | ForEach-Object { $a=Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue; [pscustomobject]@{InterfaceIndex=$_.InterfaceIndex;Alias=$a.Name;Description=$a.InterfaceDescription;Status=$a.Status;Gateway=$_.NextHop;RouteMetric=$_.RouteMetric} } | ConvertTo-Json -Compress";
        var json = await RunAsync("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 8000).ConfigureAwait(false);
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
                if (idx > 0 && gateway.Length > 0 && status.Equals("Up", StringComparison.OrdinalIgnoreCase)) list.Add(new DefaultRoute(idx, alias, desc, gateway, status, metric));
            }
        }
        catch { }
        return list;
    }

    static bool TryInstallOwnedRoute(string ip, DefaultRoute route, out string error)
    {
        error = "";
        try
        {
            if (GetExactRoute(ip)) { error = "an existing /32 route already owns this destination; refusing to overwrite it"; return false; }
            var command = $"New-NetRoute -DestinationPrefix '{ip}/32' -InterfaceIndex {route.InterfaceIndex} -NextHop '{route.Gateway}' -RouteMetric 4095 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null";
            var output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 8000);
            if (output.Contains("Exception", StringComparison.OrdinalIgnoreCase) || output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)) { error = "Windows rejected the temporary route"; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    static bool GetExactRoute(string ip)
    {
        var command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Select-Object -First 1 | ConvertTo-Json -Compress";
        var output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 5000);
        return !string.IsNullOrWhiteSpace(output) && !output.Trim().Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    static void RemoveOwnedRoute(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        var command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{ $_.RouteMetric -eq 4095 }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";
        _ = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 6000);
        if (string.Equals(appliedTarget, ip, StringComparison.OrdinalIgnoreCase)) appliedTarget = "";
    }

    static void Publish(GameRouteLabV10Form form, List<TcpTarget> candidates, TcpTarget best, bool changed)
    {
        if (form.IsDisposed || !form.IsHandleCreated || (DateTime.UtcNow - lastPublish < TimeSpan.FromSeconds(2) && !changed)) return;
        lastPublish = DateTime.UtcNow;
        try { form.BeginInvoke((Action)(() =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                type.GetField("endpoint", flags)?.SetValue(form, best.Ip);
                type.GetField("endpointPort", flags)?.SetValue(form, best.Port);
                if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{best.Ip}:{best.Port}";
                if (type.GetField("connections", flags)?.GetValue(form) is System.Collections.IList list)
                {
                    list.Clear();
                    var itemType = list.GetType().GetGenericArguments().FirstOrDefault();
                    if (itemType != null) foreach (var c in candidates.Take(10))
                    {
                        var role = ControlPorts.Contains(c.Port) ? "CONTROL" : "TCP CANDIDATE";
                        var item = Activator.CreateInstance(itemType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { c.Ip, c.Port, "TCP", $"{c.State} • {role} • seen {c.SeenCount}x" }, null);
                        if (item != null) list.Add(item);
                    }
                }
                if (type.GetField("connectionText", flags)?.GetValue(form) is Label connectionText)
                    connectionText.Text = string.Join("\r\n", candidates.Take(8).Select(c => $"TCP  {c.Ip}:{c.Port,-5}  {(ControlPorts.Contains(c.Port) ? "CONTROL" : "CANDIDATE")}  {c.State}  seen:{c.SeenCount}"));
                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                    metrics.Text = $"TARGET     {best.Ip}:{best.Port}\r\nPROTOCOL   TCP\r\nROLE       {(ControlPorts.Contains(best.Port) ? "CONTROL FALLBACK" : "TCP GAME CANDIDATE")}\r\nDISCOVERY  CROSSFIRE PROCESS FAMILY\r\nMEASURE    ROUTE PROBE ONLY\r\nPING       NOT CLAIMED\r\nCANDIDATES {candidates.Count}";
                if (type.GetField("quality", flags)?.GetValue(form) is Label quality) { quality.Text = $"● CROSSFIRE • TCP TARGET • {best.Ip}:{best.Port}"; quality.ForeColor = Color.FromArgb(40, 242, 122); }
                Log(form, $"[CROSSFIRE] {(changed ? "TARGET CHANGED" : "TARGET REFRESH")} → {best.Ip}:{best.Port} • {(ControlPorts.Contains(best.Port) ? "control fallback" : "TCP candidate")} • {candidates.Count} live/remembered TCP candidates.");
            }
            catch { }
        })); } catch { }
    }

    static void PublishWaiting(GameRouteLabV10Form form) { try { form.BeginInvoke((Action)(() => Log(form, "[CROSSFIRE] No public TCP candidates observed yet. Enter the room/match and keep the game active."))); } catch { } }
    static void StopGenericTimers(GameRouteLabV10Form form) { var flags = BindingFlags.Instance | BindingFlags.NonPublic; try { (typeof(GameRouteLabV10Form).GetField("scanTimer", flags)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop(); } catch { } try { (typeof(GameRouteLabV10Form).GetField("pingTimer", flags)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop(); } catch { } }
    static string FormatMs(double value) => value < 0 ? "unreachable" : $"{value:0.0} ms";
    static string ReadString(JsonElement x, string name) => x.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";
    static int ReadInt(JsonElement x, string name) => x.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : 0;
    static string QuotePs(string text) => "'" + text.Replace("'", "''") + "'";
    static string Run(string file, string args, int timeoutMs)
    {
        try { using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }); if (p == null) return ""; var output = p.StandardOutput.ReadToEndAsync(); var error = p.StandardError.ReadToEndAsync(); if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return output.GetAwaiter().GetResult(); } return output.GetAwaiter().GetResult() + "\r\n" + error.GetAwaiter().GetResult(); } catch { return ""; }
    }
    static async Task<string> RunAsync(string file, string args, int timeoutMs) => await Task.Run(() => Run(file, args, timeoutMs)).ConfigureAwait(false);
    static void SetField(GameRouteLabV10Form form, string name, object? value) { try { typeof(GameRouteLabV10Form).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(form, value); } catch { } }
    static void Log(GameRouteLabV10Form form, string text) { try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { } }
    static bool TryEndpoint(string value, out string ip, out int port)
    {
        ip = ""; port = 0; value = value.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal)) { var close = value.LastIndexOf(']'); if (close <= 1 || close + 2 >= value.Length) return false; ip = value[1..close]; return int.TryParse(value[(close + 2)..], out port); }
        var colon = value.LastIndexOf(':'); if (colon <= 0) return false; ip = value[..colon]; return int.TryParse(value[(colon + 1)..], out port);
    }
    static bool IsPublicIPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes(); return !(b[0] == 10 || b[0] == 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }
    readonly record struct TcpCandidate(string Ip, int Port, string State, int OwnerPid);
    readonly record struct TcpTarget(string Ip, int Port, string State, DateTime FirstSeen, DateTime LastSeen, int SeenCount, int OwnerPid);
    readonly record struct DefaultRoute(int InterfaceIndex, string Alias, string Description, string Gateway, string Status, int RouteMetric);
}
