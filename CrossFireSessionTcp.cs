using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// CrossFire room-aware TCP observer.
/// It never creates a probe socket and never uses UDP. It first captures the
/// TCP endpoints already present in the channel, then watches for a NEW
/// established TCP endpoint after the session changes. Only a persistent new
/// TCP endpoint is offered to the route optimizer. If the room adds no TCP
/// endpoint, the UI explicitly says that the room ping is not TCP-exposed.
/// </summary>
internal static class CrossFireSessionTcp
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint NO_ERROR = 0;
    private const uint TCP_STATE_ESTABLISHED = 5;
    private const int TCP_ESTATS_FINE_RTT = 8;

    private static System.Threading.Timer? _timer;
    private static int _scan;
    private static bool _baselineReady;
    private static HashSet<string> _channelEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private static string _candidateKey = "";
    private static int _candidateStreak;
    private static string _activeCandidate = "";

    public static void Apply(GameRouteLabV10Form form)
    {
        _timer?.Dispose();
        _baselineReady = false;
        _channelEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _candidateKey = "";
        _candidateStreak = 0;
        _activeCandidate = "";
        SetEndpoint(form, null, 0);
        Write(form, "[CROSSFIRE TCP] Room-aware observer armed. First stable scan becomes the channel baseline.");
        _timer = new System.Threading.Timer(_ => Tick(form), null, 700, 1500);
        form.FormClosed += (_, _) => { try { _timer?.Dispose(); } catch { } _timer = null; };
    }

    private static void Tick(GameRouteLabV10Form form)
    {
        if (Interlocked.Exchange(ref _scan, 1) != 0 || form.IsDisposed || !form.IsHandleCreated) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var process = FindCrossFireProcess();
                if (process == null)
                {
                    _baselineReady = false;
                    _channelEndpoints.Clear();
                    _candidateKey = "";
                    _candidateStreak = 0;
                    SetEndpoint(form, null, 0);
                    return;
                }

                using (process)
                {
                    SetField(form, "gamePid", process.Id);
                    SetField(form, "gameName", process.ProcessName);
                    StopGenericGameTimers(form);
                }

                var family = GetProcessFamily();
                var sockets = ReadEstablishedTcpSockets(family);
                var currentKeys = sockets.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!_baselineReady)
                {
                    if (currentKeys.Count == 0) return;
                    _channelEndpoints = currentKeys;
                    _baselineReady = true;
                    Publish(form, sockets, Array.Empty<TcpSocket>(), false);
                    Write(form, $"[CROSSFIRE TCP] Channel baseline captured: {currentKeys.Count} established TCP endpoint(s). Enter a room now; a NEW TCP endpoint will be tested if CrossFire exposes one.");
                    return;
                }

                var candidates = sockets
                    .Where(x => !IsWebEndpoint(x.Port) && !_channelEndpoints.Contains(Key(x)))
                    .ToList();

                if (candidates.Count == 0)
                {
                    _candidateKey = "";
                    _candidateStreak = 0;
                    _activeCandidate = "";
                    Publish(form, sockets, Array.Empty<TcpSocket>(), false);
                    SetEndpoint(form, null, 0);
                    return;
                }

                var chosen = candidates.OrderBy(x => x.Port).ThenBy(x => x.Ip, StringComparer.OrdinalIgnoreCase).First();
                var key = Key(chosen);
                if (!key.Equals(_candidateKey, StringComparison.OrdinalIgnoreCase))
                {
                    _candidateKey = key;
                    _candidateStreak = 1;
                }
                else
                {
                    _candidateStreak++;
                }

                var measured = new List<TcpSocket>();
                foreach (var candidate in candidates)
                {
                    var rtt = await MeasureExistingTcpRtt(candidate.Row).ConfigureAwait(false);
                    measured.Add(candidate with { RttMs = rtt });
                }

                var stable = _candidateStreak >= 3;
                Publish(form, sockets, measured, stable);
                if (stable)
                {
                    var best = measured.Where(x => x.RttMs >= 0).OrderBy(x => x.RttMs).FirstOrDefault();
                    if (best.Ip != null)
                    {
                        var activeKey = Key(best);
                        if (!activeKey.Equals(_activeCandidate, StringComparison.OrdinalIgnoreCase))
                        {
                            _activeCandidate = activeKey;
                            SetEndpoint(form, best.Ip, best.Port);
                            Write(form, $"[CROSSFIRE TCP] NEW SESSION TCP candidate: {best.Ip}:{best.Port} • existing-socket RTT {best.RttMs:0.0} ms. This is a TCP session candidate, not a claimed CrossFire room ping.");
                        }
                    }
                }
            }
            catch (Exception ex) { Write(form, "[CROSSFIRE TCP] Observer stopped safely: " + ex.Message); }
            finally { Interlocked.Exchange(ref _scan, 0); }
        });
    }

    private static Process? FindCrossFireProcess()
    {
        foreach (var p in Process.GetProcesses())
        {
            try { if (p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return p; }
            catch { p.Dispose(); }
        }
        return null;
    }

    private static HashSet<int> GetProcessFamily()
    {
        var ids = new HashSet<int>();
        string? root = null;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) continue;
                ids.Add(p.Id);
                try { root ??= Path.GetDirectoryName(p.MainModule?.FileName); } catch { }
            }
            catch { }
            finally { p.Dispose(); }
        }
        if (root == null) return ids;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (path != null && string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase)) ids.Add(p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return ids;
    }

    private static List<TcpSocket> ReadEstablishedTcpSockets(HashSet<int> pids)
    {
        var rows = GetTcpRows();
        return rows.Where(x => x.DwState == TCP_STATE_ESTABLISHED && pids.Contains((int)x.DwOwningPid))
            .Select(x => new TcpSocket(new IPAddress(x.DwRemoteAddr).ToString(), NetworkToHostPort(x.DwRemotePort), (int)x.DwOwningPid, x, -1))
            .Where(x => IsPublicIPv4(x.Ip) && x.Port > 0)
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(x => x.Ip, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Port).Take(30).ToList();
    }

    private static List<MibTcpRowOwnerPid> GetTcpRows()
    {
        var size = 0;
        var result = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (result != ERROR_INSUFFICIENT_BUFFER || size <= 0) return new();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            result = GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (result != NO_ERROR) return new();
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var list = new List<MibTcpRowOwnerPid>(count);
            for (var i = 0; i < count; i++) list.Add(Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(buffer, sizeof(int) + i * rowSize)));
            return list;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static async Task<double> MeasureExistingTcpRtt(MibTcpRowOwnerPid ownerRow)
    {
        var row = new MibTcpRow { DwState = ownerRow.DwState, DwLocalAddr = ownerRow.DwLocalAddr, DwLocalPort = ownerRow.DwLocalPort, DwRemoteAddr = ownerRow.DwRemoteAddr, DwRemotePort = ownerRow.DwRemotePort };
        var enable = Marshal.AllocHGlobal(1);
        var rod = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteByte(enable, 1);
            if (SetPerTcpConnectionEStats(ref row, TCP_ESTATS_FINE_RTT, enable, 0, 1, 0) != NO_ERROR) return -1;
            var samples = new List<double>();
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(80).ConfigureAwait(false);
                if (GetPerTcpConnectionEStats(ref row, TCP_ESTATS_FINE_RTT, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 0, rod, 0, 16) != NO_ERROR) continue;
                var stats = Marshal.PtrToStructure<TcpEstatsFineRttRodV0>(rod);
                if (stats.SumRtt > 0) samples.Add(stats.SumRtt / 1000.0);
            }
            return samples.Count == 0 ? -1 : samples.OrderBy(x => x).ElementAt(samples.Count / 2);
        }
        catch { return -1; }
        finally { Marshal.FreeHGlobal(enable); Marshal.FreeHGlobal(rod); }
    }

    private static void Publish(GameRouteLabV10Form form, List<TcpSocket> all, List<TcpSocket> candidates, bool stable)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                var text = type.GetField("connectionText", flags)?.GetValue(form) as Label;
                if (text != null)
                {
                    var lines = new List<string>();
                    foreach (var x in all.Take(10)) lines.Add($"TCP  {x.Ip}:{x.Port}  ESTABLISHED");
                    if (candidates.Count > 0)
                        lines.AddRange(candidates.Select(x => $"ROOM TCP CANDIDATE  {x.Ip}:{x.Port}  {(x.RttMs >= 0 ? $"RTT {x.RttMs:0.0} ms" : "RTT unavailable")}"));
                    text.Text = lines.Count == 0 ? "No established CrossFire TCP sockets found." : string.Join("\r\n", lines);
                }
                var metrics = type.GetField("metrics", flags)?.GetValue(form) as Label;
                if (metrics != null)
                {
                    if (stable && candidates.Count > 0)
                    {
                        var best = candidates.Where(x => x.RttMs >= 0).OrderBy(x => x.RttMs).FirstOrDefault();
                        metrics.Text = best.Ip == null ? "ROOM TCP   CANDIDATE\r\nRTT         UNAVAILABLE\r\nSTATUS      WAITING" : $"ROOM TCP   {best.Ip}:{best.Port}\r\nTCP RTT     {best.RttMs:0.0} ms\r\nSTATUS      TCP SESSION CANDIDATE";
                    }
                    else
                    {
                        metrics.Text = $"ROOM TCP   NOT EXPOSED\r\nCHANNEL TCP {all.Count}\r\nSTATUS      WAITING FOR NEW TCP SESSION";
                    }
                }
                var quality = type.GetField("quality", flags)?.GetValue(form) as Label;
                if (quality != null)
                {
                    quality.ForeColor = Color.FromArgb(132, 157, 190);
                    quality.Text = stable && candidates.Count > 0 ? "● TCP • NEW SESSION CANDIDATE" : "● ROOM TCP • NOT EXPOSED";
                }
            }));
        }
        catch { }
    }

    private static void StopGenericGameTimers(GameRouteLabV10Form form)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(GameRouteLabV10Form);
            (type.GetField("scanTimer", flags)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop();
            (type.GetField("pingTimer", flags)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop();
        }
        catch { }
    }

    private static void SetEndpoint(GameRouteLabV10Form form, string? ip, int port)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                type.GetField("endpoint", flags)?.SetValue(form, ip);
                type.GetField("endpointPort", flags)?.SetValue(form, port);
                if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = ip == null ? "ROOM TCP NOT EXPOSED" : $"{ip}:{port}";
            }));
        }
        catch { }
    }

    private static string Key(TcpSocket x) => $"{x.Ip}:{x.Port}";
    private static bool IsWebEndpoint(int port) => port is 80 or 443;
    private static void SetField(Form form, string name, object value) => form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(form, value);
    private static void Write(Form form, string message)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var console = form.GetType().GetField("console", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) as RichTextBox;
                console?.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
            }));
        }
        catch { }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool bOrder, int ulAf, int tableClass, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int estatType, IntPtr rw, uint rwVersion, uint rwSize, uint reserved);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int estatType, IntPtr rw, uint rwVersion, uint rwSize, IntPtr ros, uint rosVersion, uint rosSize, IntPtr rod, uint rodVersion, uint rodSize);

    [StructLayout(LayoutKind.Sequential)] private struct MibTcpRowOwnerPid { public uint DwState, DwLocalAddr, DwLocalPort, DwRemoteAddr, DwRemotePort, DwOwningPid; }
    [StructLayout(LayoutKind.Sequential)] private struct MibTcpRow { public uint DwState, DwLocalAddr, DwLocalPort, DwRemoteAddr, DwRemotePort; }
    [StructLayout(LayoutKind.Sequential)] private struct TcpEstatsFineRttRodV0 { public long SumRtt; public long CountRtt; public long CurRtt; public long MaxRtt; public long MinRtt; public long VarRtt; public long Smoothing; public long BaseRtt; }

    private static int NetworkToHostPort(uint value) => (int)IPAddress.NetworkToHostOrder(unchecked((short)value)) & 0xFFFF;
    private static bool IsPublicIPv4(string ip) => IPAddress.TryParse(ip, out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any);
    private sealed record TcpSocket(string Ip, int Port, int Pid, MibTcpRowOwnerPid Row, double RttMs)
    {
        public string State => "ESTABLISHED";
    }
}
