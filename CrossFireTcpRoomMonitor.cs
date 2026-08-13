using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// TCP-only CrossFire room monitor.
///
/// Important design rule: an established TCP connection is never renamed a
/// "room" just because it has the lowest RTT. The monitor first records a
/// stable channel baseline, then looks for a genuinely NEW TCP session. All
/// existing TCP sessions remain visible with Windows' own TCP path RTT.
/// No UDP, ICMP, synthetic TCP connection, or packet probe is used here.
/// </summary>
internal static class CrossFireTcpRoomMonitor
{
    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint NO_ERROR = 0;
    private const uint TCP_STATE_ESTABLISHED = 5;
    private const int TCP_ESTATS_PATH = 3;

    private static System.Threading.Timer? timer;
    private static int running;
    private static bool baselineReady;
    private static HashSet<string> baseline = new(StringComparer.OrdinalIgnoreCase);
    private static string lastRoomKey = "";
    private static int sameCandidateScans;
    private static bool warnedNoRoomTcp;

    public static void Apply(GameRouteLabV10Form form)
    {
        timer?.Dispose();
        baselineReady = false;
        baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lastRoomKey = "";
        sameCandidateScans = 0;
        warnedNoRoomTcp = false;

        StopGenericTimers(form);
        Write(form, "[CROSSFIRE TCP] TCP-only room monitor started. Existing TCP sessions will stay visible with real Windows TCP path RTT.");
        Write(form, "[CROSSFIRE TCP] To test room discovery correctly: stay in the CrossFire channel first, let the baseline settle, then enter the room.");
        timer = new System.Threading.Timer(_ => Tick(form), null, 500, 1200);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
    }

    private static void Tick(GameRouteLabV10Form form)
    {
        if (Interlocked.Exchange(ref running, 1) != 0 || form.IsDisposed || !form.IsHandleCreated)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var game = FindCrossFireProcess();
                if (game == null)
                {
                    baselineReady = false;
                    baseline.Clear();
                    sameCandidateScans = 0;
                    warnedNoRoomTcp = false;
                    Publish(form, new List<TcpSocket>(), new List<TcpSocket>(), false, false);
                    return;
                }

                SetField(form, "gamePid", game.Id);
                SetField(form, "gameName", game.ProcessName);

                var family = GetProcessFamily();
                var sockets = ReadSockets(family);
                var currentKeys = sockets.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!baselineReady)
                {
                    if (currentKeys.Count == 0) return;
                    baseline = currentKeys;
                    baselineReady = true;
                    var measuredBaseline = await MeasureAll(sockets).ConfigureAwait(false);
                    Publish(form, measuredBaseline, new List<TcpSocket>(), false, true);
                    Write(form, $"[CROSSFIRE TCP] Channel TCP baseline locked: {currentKeys.Count} established session(s). Room detection now watches for a NEW TCP session.");
                    return;
                }

                var candidates = sockets
                    .Where(x => !baseline.Contains(Key(x)))
                    .Where(x => !IsWebPort(x.Port))
                    .ToList();

                var measured = await MeasureAll(sockets).ConfigureAwait(false);
                var measuredCandidates = measured.Where(x => candidates.Any(c => Key(c).Equals(Key(x), StringComparison.OrdinalIgnoreCase))).ToList();

                if (measuredCandidates.Count == 0)
                {
                    sameCandidateScans = 0;
                    Publish(form, measured, new List<TcpSocket>(), false, false);
                    if (!warnedNoRoomTcp)
                    {
                        warnedNoRoomTcp = true;
                        Write(form, "[CROSSFIRE TCP] No NEW CrossFire TCP session observed. The visible TCP session(s) are channel/control sessions; no room TCP RTT is being invented.");
                    }
                    return;
                }

                var chosen = measuredCandidates
                    .Where(x => x.RttMs >= 0)
                    .OrderBy(x => x.RttMs)
                    .ThenBy(x => x.Port)
                    .FirstOrDefault();

                var candidateKey = chosen is null ? Key(measuredCandidates[0]) : Key(chosen);
                if (candidateKey.Equals(lastRoomKey, StringComparison.OrdinalIgnoreCase)) sameCandidateScans++;
                else { lastRoomKey = candidateKey; sameCandidateScans = 1; }

                var stable = sameCandidateScans >= 3;
                Publish(form, measured, measuredCandidates, stable, false);

                if (stable && chosen is not null)
                {
                    SetEndpoint(form, chosen.Ip, chosen.Port);
                    Write(form, $"[CROSSFIRE TCP] NEW ROOM TCP SESSION confirmed: {chosen.Ip}:{chosen.Port} • Windows TCP path RTT {FormatRtt(chosen.RttMs)}.");
                }
            }
            catch (Exception ex)
            {
                Write(form, "[CROSSFIRE TCP] Monitor error (safe): " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref running, 0);
            }
        });
    }

    private static async Task<List<TcpSocket>> MeasureAll(List<TcpSocket> sockets)
    {
        var result = new List<TcpSocket>(sockets.Count);
        foreach (var socket in sockets)
        {
            var rtt = await ReadExistingTcpPathRtt(socket.Row).ConfigureAwait(false);
            result.Add(socket with { RttMs = rtt });
        }
        return result;
    }

    private static Process? FindCrossFireProcess()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch { p.Dispose(); }
        }
        return null;
    }

    private static HashSet<int> GetProcessFamily()
    {
        var ids = new HashSet<int>();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (!p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) continue;
                ids.Add(p.Id);
                try
                {
                    var path = p.MainModule?.FileName;
                    var root = path == null ? null : Path.GetDirectoryName(path);
                    if (!string.IsNullOrWhiteSpace(root)) roots.Add(root);
                }
                catch { }
            }
            catch { }
            finally { p.Dispose(); }
        }

        if (roots.Count == 0) return ids;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { }
                var root = path == null ? null : Path.GetDirectoryName(path);
                if (root != null && roots.Contains(root)) ids.Add(p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return ids;
    }

    private static List<TcpSocket> ReadSockets(HashSet<int> pids)
    {
        var rows = GetTcpRows();
        return rows
            .Where(x => x.DwState == TCP_STATE_ESTABLISHED && pids.Contains((int)x.DwOwningPid))
            .Select(x => new TcpSocket(
                new IPAddress(x.DwRemoteAddr).ToString(),
                NetworkToHostPort(x.DwRemotePort),
                NetworkToHostPort(x.DwLocalPort),
                (int)x.DwOwningPid,
                x,
                -1))
            .Where(x => IsPublicIPv4(x.Ip) && x.Port > 0 && x.LocalPort > 0)
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Ip, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Port)
            .Take(40)
            .ToList();
    }

    private static List<MibTcpRowOwnerPid> GetTcpRows()
    {
        var size = 0;
        var rc = GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (rc != ERROR_INSUFFICIENT_BUFFER || size <= 0) return new();

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            rc = GetExtendedTcpTable(buffer, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (rc != NO_ERROR) return new();
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var result = new List<MibTcpRowOwnerPid>(count);
            for (var i = 0; i < count; i++)
                result.Add(Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(buffer, sizeof(int) + i * rowSize)));
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static async Task<double> ReadExistingTcpPathRtt(MibTcpRowOwnerPid ownerRow)
    {
        var row = new MibTcpRow
        {
            DwState = ownerRow.DwState,
            DwLocalAddr = ownerRow.DwLocalAddr,
            DwLocalPort = ownerRow.DwLocalPort,
            DwRemoteAddr = ownerRow.DwRemoteAddr,
            DwRemotePort = ownerRow.DwRemotePort
        };

        var rw = Marshal.AllocHGlobal(1);
        var rodSize = Marshal.SizeOf<TcpEstatsPathRodV0>();
        var rod = Marshal.AllocHGlobal(rodSize);
        try
        {
            Marshal.WriteByte(rw, 1);
            var enableRc = SetPerTcpConnectionEStats(ref row, TCP_ESTATS_PATH, rw, 0, 1, 0);
            if (enableRc != NO_ERROR) return -1;

            await Task.Delay(60).ConfigureAwait(false);

            var rwRead = Marshal.AllocHGlobal(1);
            try
            {
                var rc = GetPerTcpConnectionEStats(ref row, TCP_ESTATS_PATH,
                    rwRead, 0, 1,
                    IntPtr.Zero, 0, 0,
                    rod, 0, (uint)rodSize);
                if (rc != NO_ERROR || Marshal.ReadByte(rwRead) == 0) return -1;
            }
            finally { Marshal.FreeHGlobal(rwRead); }

            var stats = Marshal.PtrToStructure<TcpEstatsPathRodV0>(rod);
            if (stats.SampleRtt > 0) return stats.SampleRtt;
            if (stats.SmoothedRtt > 0) return stats.SmoothedRtt;
            if (stats.MinRtt > 0) return stats.MinRtt;
            return -1;
        }
        catch { return -1; }
        finally
        {
            Marshal.FreeHGlobal(rw);
            Marshal.FreeHGlobal(rod);
        }
    }

    private static void Publish(GameRouteLabV10Form form, List<TcpSocket> all, List<TcpSocket> candidates, bool stable, bool baselineScan)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                if (type.GetField("connectionText", flags)?.GetValue(form) is Label connection)
                {
                    var lines = all.Take(10).Select(x =>
                        $"TCP  {x.Ip}:{x.Port,-5}  ESTABLISHED  RTT {FormatRtt(x.RttMs)}").ToList();
                    if (candidates.Count > 0)
                        lines.AddRange(candidates.Select(x =>
                            $"ROOM TCP CANDIDATE  {x.Ip}:{x.Port}  RTT {FormatRtt(x.RttMs)}"));
                    connection.Text = lines.Count == 0 ? "No public CrossFire TCP session found." : string.Join("\r\n", lines);
                }

                if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                {
                    if (stable && candidates.Count > 0)
                    {
                        var best = candidates.Where(x => x.RttMs >= 0).OrderBy(x => x.RttMs).FirstOrDefault();
                        metrics.Text = best is null
                            ? "ROOM TCP   CANDIDATE\r\nTCP RTT     UNAVAILABLE\r\nSTATUS      TCP SESSION FOUND"
                            : $"ROOM TCP   {best.Ip}:{best.Port}\r\nTCP RTT     {best.RttMs:0.0} ms\r\nSOURCE      EXISTING TCP PATH\r\nSTATUS      ROOM TCP CONFIRMED";
                    }
                    else
                    {
                        var first = all.Where(x => x.RttMs >= 0).OrderBy(x => x.RttMs).FirstOrDefault();
                        var channelRtt = first is null ? "—" : $"{first.RttMs:0.0} ms";
                        metrics.Text = baselineScan
                            ? $"CHANNEL TCP {all.Count}\r\nCHANNEL RTT {channelRtt}\r\nROOM TCP    WAITING\r\nSTATUS      BASELINE LOCKED"
                            : $"CHANNEL TCP {all.Count}\r\nCHANNEL RTT {channelRtt}\r\nROOM TCP    NOT OBSERVED\r\nSTATUS      WAITING FOR NEW TCP";
                    }
                }

                if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                {
                    quality.ForeColor = stable && candidates.Count > 0 ? Color.FromArgb(40, 242, 122) : Color.FromArgb(132, 157, 190);
                    quality.Text = stable && candidates.Count > 0
                        ? "● ROOM TCP • EXISTING SESSION RTT"
                        : "● TCP CHANNEL MONITOR • ROOM TCP NOT OBSERVED";
                }
            }));
        }
        catch { }
    }

    private static void SetEndpoint(GameRouteLabV10Form form, string ip, int port)
    {
        try
        {
            form.BeginInvoke((Action)(() =>
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(GameRouteLabV10Form);
                type.GetField("endpoint", flags)?.SetValue(form, ip);
                type.GetField("endpointPort", flags)?.SetValue(form, port);
                if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{ip}:{port}";
            }));
        }
        catch { }
    }

    private static void StopGenericTimers(GameRouteLabV10Form form)
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

    private static void SetField(Form form, string name, object value)
        => form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(form, value);

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

    private static bool IsWebPort(int port) => port is 80 or 443 or 8080 or 8443;
    private static string Key(TcpSocket x) => $"{x.Ip}:{x.Port}|LOCAL:{x.LocalPort}";
    private static string FormatRtt(double rtt) => rtt >= 0 ? $"{rtt:0.0} ms" : "—";
    private static int NetworkToHostPort(uint value) => (int)IPAddress.NetworkToHostOrder(unchecked((short)value)) & 0xFFFF;
    private static bool IsPublicIPv4(string ip)
        => IPAddress.TryParse(ip, out var a)
        && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && !IPAddress.IsLoopback(a)
        && !a.Equals(IPAddress.Any)
        && !(a.GetAddressBytes()[0] == 10)
        && !(a.GetAddressBytes()[0] == 192 && a.GetAddressBytes()[1] == 168)
        && !(a.GetAddressBytes()[0] == 172 && a.GetAddressBytes()[1] >= 16 && a.GetAddressBytes()[1] <= 31);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen, bool bOrder, int ulAf, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SetPerTcpConnectionEStats(ref MibTcpRow row, int estatType, IntPtr rw, uint rwVersion, uint rwSize, uint offset);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetPerTcpConnectionEStats(ref MibTcpRow row, int estatType,
        IntPtr rw, uint rwVersion, uint rwSize,
        IntPtr ros, uint rosVersion, uint rosSize,
        IntPtr rod, uint rodVersion, uint rodSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint DwState, DwLocalAddr, DwLocalPort, DwRemoteAddr, DwRemotePort, DwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRow
    {
        public uint DwState, DwLocalAddr, DwLocalPort, DwRemoteAddr, DwRemotePort;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpEstatsPathRodV0
    {
        public uint FastRetran, Timeouts, SubsequentTimeouts, CurTimeoutCount, AbruptTimeouts;
        public uint PktsRetrans, BytesRetrans, DupAcksIn, SacksRcvd, SackBlocksRcvd;
        public uint CongSignals, PreCongSumCwnd, PreCongSumRtt, PostCongSumRtt, PostCongCountRtt;
        public uint EcnSignals, EceRcvd, SendStall, QuenchRcvd, RetranThresh;
        public uint SndDupAckEpisodes, SumBytesReordered, NonRecovDa, NonRecovDaEpisodes, AckAfterFr;
        public uint DsackDups, SampleRtt, SmoothedRtt, RttVar, MaxRtt;
        public uint MinRtt, SumRtt, CountRtt, CurRto, MaxRto;
        public uint MinRto, CurMss, MaxMss, MinMss, SpuriousRtoDetections;
    }

    private sealed record TcpSocket(string Ip, int Port, int LocalPort, int Pid, MibTcpRowOwnerPid Row, double RttMs)
    {
        public string State => "ESTABLISHED";
    }
}
