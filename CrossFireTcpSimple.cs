using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CrossFireRouteLab;

/// <summary>
/// Simple CrossFire TCP layer.
/// It only does three things: find the live CrossFire TCP sockets, read RTT from
/// those existing sockets using Windows TCP extended statistics, and hand the
/// selected TCP target to the route optimizer. It never creates a new TCP socket
/// to pretend that a connect time is the game's RTT.
/// </summary>
internal static class CrossFireTcpSimple
{
    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;
    private const uint TcpStateEstablished = 5;
    private const int TcpConnectionEstatsFineRtt = 8;

    private static System.Threading.Timer? timer;
    private static int scanRunning;
    private static string targetKey = "";
    private static DateTime lastOptimize = DateTime.MinValue;
    private static string appliedTarget = "";

    public static void Apply(GameRouteLabV10Form form)
    {
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 500, 1500);
        form.FormClosed += (_, _) =>
        {
            try { timer?.Dispose(); } catch { }
            timer = null;
            if (!string.IsNullOrWhiteSpace(appliedTarget)) RemoveOwnedRoute(appliedTarget);
        };
        Log(form, "[CROSSFIRE TCP] Simple TCP reader armed. Existing sockets only; no synthetic TCP ping.");
    }

    private static void Tick(GameRouteLabV10Form form)
    {
        if (Interlocked.Exchange(ref scanRunning, 1) != 0 || form.IsDisposed || !form.IsHandleCreated) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var process = FindCrossFireProcess();
                if (process == null) return;
                using (process)
                {
                    StopGenericTimers(form);
                    SetField(form, "gamePid", process.Id);
                    SetField(form, "gameName", process.ProcessName);
                }

                var family = GetCrossFireProcessFamily();
                var sockets = ReadEstablishedTcpSockets(family);
                var measured = new List<TcpSocket>();
                foreach (var socket in sockets)
                {
                    var rtt = await MeasureExistingTcpRtt(socket.Row).ConfigureAwait(false);
                    measured.Add(socket with { RttMs = rtt });
                }

                Publish(form, measured);

                var usable = measured.Where(x => x.RttMs >= 0).OrderBy(x => x.RttMs).ToList();
                var target = usable.FirstOrDefault();
                if (target.Ip == null)
                {
                    target = measured.FirstOrDefault();
                    if (target.Ip == null) return;
                }

                var key = $"{target.Ip}:{target.Port}";
                if (!string.Equals(targetKey, key, StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow - lastOptimize > TimeSpan.FromMinutes(3))
                {
                    targetKey = key;
                    lastOptimize = DateTime.UtcNow;
                    await OptimizeRoute(form, target.Ip, target.Port).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log(form, "[CROSSFIRE TCP] Stopped safely: " + ex.Message);
            }
            finally { Interlocked.Exchange(ref scanRunning, 0); }
        });
    }

    private static Process? FindCrossFireProcess()
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return p;
                }
                catch { p.Dispose(); }
            }
        }
        catch { }
        return null;
    }

    private static HashSet<int> GetCrossFireProcessFamily()
    {
        var result = new HashSet<int>();
        string? rootDirectory = null;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(p.Id);
                try { rootDirectory ??= Path.GetDirectoryName(p.MainModule?.FileName); } catch { }
            }
            catch { }
            finally { p.Dispose(); }
        }

        if (string.IsNullOrWhiteSpace(rootDirectory)) return result;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (!string.IsNullOrWhiteSpace(path) &&
                    string.Equals(Path.GetDirectoryName(path), rootDirectory, StringComparison.OrdinalIgnoreCase))
                    result.Add(p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return result;
    }

    private static List<TcpSocket> ReadEstablishedTcpSockets(HashSet<int> pids)
    {
        var rows = GetTcpRows();
        return rows
            .Where(x => x.DwState == TcpStateEstablished && pids.Contains((int)x.DwOwningPid))
            .Select(x =>
            {
                var ip = new IPAddress(x.DwRemoteAddr).ToString();
                var port = NetworkToHostPort(x.DwRemotePort);
                return new TcpSocket(ip, port, (int)x.DwOwningPid, x, -1);
            })
            .Where(x => IsPublicIPv4(x.Ip) && x.Port > 0)
            .GroupBy(x => $"{x.Ip}:{x.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Ip, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Port)
            .Take(30)
            .ToList();
    }

    private static List<MibTcpRowOwnerPid> GetTcpRows()
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
        if (result != ErrorInsufficientBuffer || size <= 0) return new List<MibTcpRowOwnerPid>();

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, true, AfInet, TcpTableOwnerPidAll, 0);
            if (result != NoError) return new List<MibTcpRowOwnerPid>();

            var count = Marshal.ReadInt32(buffer);
            var rows = new List<MibTcpRowOwnerPid>(Math.Max(0, count));
            var offset = Marshal.SizeOf<int>();
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (var i = 0; i < count; i++)
            {
                var ptr = IntPtr.Add(buffer, offset + i * rowSize);
                rows.Add(Marshal.PtrToStructure<MibTcpRowOwnerPid>(ptr));
            }
            return rows;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static async Task<double> MeasureExistingTcpRtt(MibTcpRowOwnerPid ownerRow)
    {
        var row = new MibTcpRow
        {
            DwState = ownerRow.DwState,
            DwLocalAddr = ownerRow.DwLocalAddr,
            DwLocalPort = ownerRow.DwLocalPort,
            DwRemoteAddr = ownerRow.DwRemoteAddr,
            DwRemotePort = ownerRow.DwRemotePort
        };

        var enable = Marshal.AllocHGlobal(1);
        var rod = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteByte(enable, 1);
            var set = SetPerTcpConnectionEStats(ref row, TcpConnectionEstatsFineRtt, enable, 0, 1, 0);
            if (set != NoError) return -1;

            var samples = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(80).ConfigureAwait(false);
                var get = GetPerTcpConnectionEStats(ref row, TcpConnectionEstatsFineRtt,
                    IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, rod, 0, 16);
                if (get != NoError) continue;
                var stats = Marshal.PtrToStructure<TcpEstatsFineRttRodV0>(rod);
                if (stats.SumRtt > 0) samples.Add(stats.SumRtt / 1000.0);
            }
            return samples.Count == 0 ? -1 : samples.OrderBy(x => x).ElementAt(samples.Count / 2);
        }
        catch { return -1; }
        finally
        {
            Marshal.FreeHGlobal(enable);
            Marshal.FreeHGlobal(rod);
        }
    }

    private static void Publish(GameRouteLabV10Form form, List<TcpSocket> sockets)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                try
                {
                    var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    var type = typeof(GameRouteLabV10Form);
                    var listObject = type.GetField("connections", flags)?.GetValue(form);
                    if (listObject is System.Collections.IList list)
                    {
                        list.Clear();
                        var itemType = listObject.GetType().GetGenericArguments().FirstOrDefault();
                        if (itemType != null)
                        {
                            foreach (var socket in sockets)
                            {
                                var rtt = socket.RttMs >= 0 ? $"RTT {socket.RttMs:0.0} ms" : "RTT unavailable";
                                var item = Activator.CreateInstance(itemType,
                                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                    null,
                                    new object[] { socket.Ip, socket.Port, "TCP", $"{socket.State} • {rtt}" },
                                    null);
                                if (item != null) list.Add(item);
                            }
                        }
                    }

                    var connectionText = type.GetField("connectionText", flags)?.GetValue(form) as Label;
                    if (connectionText != null)
                    {
                        connectionText.Text = sockets.Count == 0
                            ? "No established CrossFire TCP sockets found."
                            : string.Join("\r\n", sockets.Take(10).Select(x =>
                                $"TCP  {x.Ip}:{x.Port}  ESTABLISHED  {(x.RttMs >= 0 ? $"RTT {x.RttMs:0.0} ms" : "RTT unavailable")}"));
                    }

                    var best = sockets.Where(x => x.RttMs >= 0).OrderBy(x => x.RttMs).FirstOrDefault();
                    var selected = best.Ip == null ? sockets.FirstOrDefault() : best;
                    if (selected.Ip != null)
                    {
                        type.GetField("endpoint", flags)?.SetValue(form, selected.Ip);
                        type.GetField("endpointPort", flags)?.SetValue(form, selected.Port);
                        if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{selected.Ip}:{selected.Port}";
                        if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                            metrics.Text = $"TCP TARGET   {selected.Ip}:{selected.Port}\r\nTCP RTT      {(selected.RttMs >= 0 ? $"{selected.RttMs:0.0} ms" : "UNAVAILABLE")}\r\nTCP SOCKETS  {sockets.Count}\r\nSOURCE       EXISTING WINDOWS TCP SOCKET";
                        if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                        {
                            quality.Text = $"● TCP • {selected.Ip}:{selected.Port} • {(selected.RttMs >= 0 ? $"{selected.RttMs:0.0} ms" : "RTT unavailable")}";
                            quality.ForeColor = Color.FromArgb(40, 242, 122);
                        }
                        Log(form, $"[CROSSFIRE TCP] {sockets.Count} established TCP socket(s). Target {selected.Ip}:{selected.Port}. {(selected.RttMs >= 0 ? $"Existing-socket RTT {selected.RttMs:0.0} ms." : "Existing-socket RTT unavailable.")}");
                    }
                    else
                    {
                        Log(form, "[CROSSFIRE TCP] No established TCP socket is currently exposed by CrossFire.");
                    }
                }
                catch { }
            }));
        }
        catch { }
    }

    private static async Task OptimizeRoute(GameRouteLabV10Form form, string ip, int port)
    {
        try
        {
            var routes = await ReadDefaultRoutes().ConfigureAwait(false);
            var candidates = routes.GroupBy(x => x.InterfaceIndex).Select(g => g.OrderBy(x => x.RouteMetric).First())
                .Where(x => x.Gateway.Length > 0 && x.InterfaceIndex > 0 && x.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count <= 1)
            {
                Log(form, candidates.Count == 1
                    ? $"[ROUTE AI] {candidates[0].Alias} is the only usable default path. No alternate route to compare."
                    : "[ROUTE AI] No usable default path was found.");
                return;
            }

            var baseline = await MeasureNewTcpRoute(ip, port, 5).ConfigureAwait(false);
            var results = new List<(DefaultRoute Route, double Ms)>();
            foreach (var route in candidates)
            {
                if (!TryInstallOwnedRoute(ip, route, out var error))
                {
                    Log(form, $"[ROUTE AI] {route.Alias}: skipped — {error}");
                    continue;
                }
                try
                {
                    var ms = await MeasureNewTcpRoute(ip, port, 5).ConfigureAwait(false);
                    results.Add((route, ms));
                    Log(form, $"[ROUTE AI] {route.Alias} → {route.Gateway}: {(ms >= 0 ? $"{ms:0.0} ms" : "unreachable")}");
                }
                finally { RemoveOwnedRoute(ip); }
            }

            var valid = results.Where(x => x.Ms >= 0).OrderBy(x => x.Ms).ToList();
            if (valid.Count == 0 || (baseline >= 0 && valid[0].Ms >= baseline - 3.0))
            {
                Log(form, "[ROUTE AI] No alternate route produced a material TCP improvement. Routing unchanged.");
                return;
            }

            if (!TryInstallOwnedRoute(ip, valid[0].Route, out var applyError))
            {
                Log(form, "[ROUTE AI] Apply failed: " + applyError);
                return;
            }
            appliedTarget = ip;
            Log(form, $"[ROUTE AI] APPLIED {valid[0].Route.Alias} → {valid[0].Route.Gateway} for {ip}/32.");
        }
        catch (Exception ex) { Log(form, "[ROUTE AI] Stopped safely: " + ex.Message); }
    }

    private static async Task<double> MeasureNewTcpRoute(string ip, int port, int count)
    {
        var values = new List<double>();
        for (var i = 0; i < count; i++)
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
            await Task.Delay(100).ConfigureAwait(false);
        }
        return values.Count == 0 ? -1 : values.OrderBy(x => x).ElementAt(values.Count / 2);
    }

    private static async Task<List<DefaultRoute>> ReadDefaultRoutes()
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

    private static bool TryInstallOwnedRoute(string ip, DefaultRoute route, out string error)
    {
        error = "";
        try
        {
            if (GetExactRoute(ip)) { error = "an existing /32 route owns this destination; refusing to overwrite it"; return false; }
            var command = $"New-NetRoute -DestinationPrefix '{ip}/32' -InterfaceIndex {route.InterfaceIndex} -NextHop '{route.Gateway}' -RouteMetric 4095 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null";
            var output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 8000);
            if (output.Contains("Exception", StringComparison.OrdinalIgnoreCase) || output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)) { error = "Windows rejected the temporary route"; return false; }
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static bool GetExactRoute(string ip)
    {
        var command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Select-Object -First 1 | ConvertTo-Json -Compress";
        var output = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 5000);
        return !string.IsNullOrWhiteSpace(output) && !output.Trim().Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveOwnedRoute(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        var command = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{ $_.RouteMetric -eq 4095 }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";
        _ = Run("powershell.exe", "-NoProfile -NonInteractive -Command " + QuotePs(command), 6000);
        if (string.Equals(appliedTarget, ip, StringComparison.OrdinalIgnoreCase)) appliedTarget = "";
    }

    private static void StopGenericTimers(GameRouteLabV10Form form)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try { (typeof(GameRouteLabV10Form).GetField("scanTimer", flags)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop(); } catch { }
        try { (typeof(GameRouteLabV10Form).GetField("pingTimer", flags)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop(); } catch { }
    }

    private static string ReadString(JsonElement x, string name) => x.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";
    private static int ReadInt(JsonElement x, string name) => x.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : 0;
    private static string QuotePs(string text) => "'" + text.Replace("'", "''") + "'";

    private static string Run(string file, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return output.GetAwaiter().GetResult(); }
            return output.GetAwaiter().GetResult() + "\r\n" + error.GetAwaiter().GetResult();
        }
        catch { return ""; }
    }

    private static async Task<string> RunAsync(string file, string args, int timeoutMs) => await Task.Run(() => Run(file, args, timeoutMs)).ConfigureAwait(false);
    private static void SetField(GameRouteLabV10Form form, string name, object? value) { try { typeof(GameRouteLabV10Form).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(form, value); } catch { } }
    private static void Log(GameRouteLabV10Form form, string text) { try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { } }

    private static ushort NetworkToHostPort(uint value)
    {
        var v = (ushort)(value & 0xFFFF);
        return (ushort)((v >> 8) | (v << 8));
    }

    private static bool IsPublicIPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return false;
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127));
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int estatsType, IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint DwState;
        public uint DwLocalAddr;
        public uint DwLocalPort;
        public uint DwRemoteAddr;
        public uint DwRemotePort;
        public uint DwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint DwState;
        public uint DwLocalAddr;
        public uint DwLocalPort;
        public uint DwRemoteAddr;
        public uint DwRemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpEstatsFineRttRodV0
    {
        public uint RttVar;
        public uint MaxRtt;
        public uint MinRtt;
        public uint SumRtt;
    }

    private readonly record struct TcpSocket(string Ip, int Port, int OwnerPid, MibTcpRowOwnerPid Row, double RttMs)
    {
        public string State => "ESTABLISHED";
    }

    private readonly record struct DefaultRoute(int InterfaceIndex, string Alias, string Description, string Gateway, string Status, int RouteMetric);
}
