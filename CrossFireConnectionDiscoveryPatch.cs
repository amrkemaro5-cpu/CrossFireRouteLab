using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

/// <summary>
/// CrossFire-specific connection discovery layer.
///
/// The v10 dashboard was still reading only sockets whose PID exactly matched
/// the selected game process. CrossFire can expose the lobby/master socket in
/// one process while the active room/session sockets are owned by a sibling or
/// helper process from the same installation directory. This patch discovers
/// the whole CrossFire process family and keeps a short-lived history so short
/// room connections are not lost between two netstat snapshots.
/// </summary>
internal static class CrossFireConnectionDiscoveryPatch
{
    static System.Threading.Timer? timer;
    static bool running;
    static readonly object sync = new();
    static DateTime lastScanUtc = DateTime.MinValue;
    static readonly Dictionary<string, SeenEndpoint> seen = new(StringComparer.OrdinalIgnoreCase);

    static readonly string[] CrossFireNames =
    {
        "crossfire", "crossfire_x64", "crossfire64", "crossfireclient", "crossfireclient64"
    };

    static readonly string[] NoiseNames =
    {
        "gameroutelab", "steamwebhelper", "steam", "chrome", "msedge", "firefox",
        "discord", "powershell", "pwsh", "cmd", "conhost", "svchost", "explorer",
        "searchhost", "searchindexer", "runtimebroker", "onedrive"
    };

    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        timer = new System.Threading.Timer(_ => Tick(form), null, 1500, 1200);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[CROSSFIRE] Room-aware connection discovery enabled: main + helper-process TCP sockets are tracked together.");
    }

    static void Tick(Form form)
    {
        if (running || form.IsDisposed || !form.IsHandleCreated) return;
        if (DateTime.UtcNow - lastScanUtc < TimeSpan.FromMilliseconds(900)) return;

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();
        if (type.GetField("gamePid", flags)?.GetValue(form) is not int pid || pid <= 0) return;
        var gameName = type.GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (!gameName.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;

        lastScanUtc = DateTime.UtcNow;
        running = true;
        _ = Task.Run(() => ScanAndPublish(form, pid));
    }

    static async Task ScanAndPublish(Form form, int gamePid)
    {
        try
        {
            var family = await Task.Run(() => DiscoverFamily(gamePid)).ConfigureAwait(false);
            if (family.Count == 0) return;

            var text = await RunAsync("netstat.exe", "-n -o -p tcp", 2500).ConfigureAwait(false);
            var endpoints = ParseTcp(text, family);

            var now = DateTime.UtcNow;
            lock (sync)
            {
                foreach (var endpoint in endpoints)
                {
                    var key = $"{endpoint.Ip}:{endpoint.Port}";
                    seen[key] = new SeenEndpoint(endpoint.Ip, endpoint.Port, endpoint.Pid, endpoint.ProcessName, endpoint.State, now);
                }

                foreach (var stale in seen.Where(x => now - x.Value.LastSeenUtc > TimeSpan.FromSeconds(18)).Select(x => x.Key).ToList())
                    seen.Remove(stale);
            }

            var current = endpoints
                .Select(x => new Candidate(x.Ip, x.Port, x.Pid, x.ProcessName, x.State, now))
                .ToList();

            lock (sync)
            {
                foreach (var item in seen.Values)
                {
                    if (current.Any(x => x.Ip == item.Ip && x.Port == item.Port)) continue;
                    if (now - item.LastSeenUtc <= TimeSpan.FromSeconds(18))
                        current.Add(new Candidate(item.Ip, item.Port, item.Pid, item.ProcessName, $"SEEN {Math.Max(1, (int)(now - item.LastSeenUtc).TotalSeconds)}s AGO", item.LastSeenUtc));
                }
            }

            var ranked = current
                .GroupBy(x => $"{x.Ip}:{x.Port}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.LastSeenUtc).First())
                .OrderByDescending(x => x.State.StartsWith("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.Port)
                .Take(30)
                .ToList();

            Publish(form, ranked);
        }
        catch (Exception ex)
        {
            Log(form, "[CROSSFIRE] Connection discovery stopped safely: " + ex.Message);
        }
        finally { running = false; }
    }

    static HashSet<int> DiscoverFamily(int rootPid)
    {
        var result = new HashSet<int> { rootPid };
        try
        {
            using var root = Process.GetProcessById(rootPid);
            var rootPath = SafePath(root);
            var rootDir = string.IsNullOrWhiteSpace(rootPath) ? "" : Path.GetDirectoryName(rootPath) ?? "";
            var rootName = root.ProcessName;

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var name = p.ProcessName;
                    if (NoiseNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
                    var path = SafePath(p);
                    var sameCrossFireName = IsCrossFireName(name);
                    var sameInstall = !string.IsNullOrWhiteSpace(rootDir) && !string.IsNullOrWhiteSpace(path) &&
                                      Path.GetDirectoryName(path)?.Equals(rootDir, StringComparison.OrdinalIgnoreCase) == true;
                    var sameExe = name.Equals(rootName, StringComparison.OrdinalIgnoreCase);
                    if (sameCrossFireName || sameInstall || sameExe) result.Add(p.Id);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        return result;
    }

    static List<TcpEndpoint> ParseTcp(string text, HashSet<int> family)
    {
        var result = new List<TcpEndpoint>();
        foreach (var line in text.Replace('\r', '\n').Split('\n'))
        {
            var m = Regex.Match(line, @"^\s*TCP\s+(\S+)\s+(\S+)\s+(\S+)\s+(\d+)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[4].Value, out var pid) || !family.Contains(pid)) continue;
            var state = m.Groups[3].Value.Trim();
            if (!state.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase) &&
                !state.Equals("SYN_SENT", StringComparison.OrdinalIgnoreCase) &&
                !state.Equals("SYN_RECEIVED", StringComparison.OrdinalIgnoreCase)) continue;

            if (!TrySplitEndpoint(m.Groups[2].Value, out var ip, out var port)) continue;
            if (!IsPublicIPv4(ip) || port <= 0) continue;

            var name = "crossfire";
            try { using var p = Process.GetProcessById(pid); name = p.ProcessName; } catch { }
            result.Add(new TcpEndpoint(ip, port, pid, name, state));
        }
        return result;
    }

    static bool TrySplitEndpoint(string value, out string ip, out int port)
    {
        ip = ""; port = 0;
        value = value.Trim();
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            var close = value.LastIndexOf(']');
            if (close <= 1) return false;
            ip = value[1..close];
            return close + 2 < value.Length && int.TryParse(value[(close + 2)..], out port);
        }
        var colon = value.LastIndexOf(':');
        if (colon <= 0) return false;
        ip = value[..colon];
        return int.TryParse(value[(colon + 1)..], out port);
    }

    static string SafePath(Process p)
    {
        try { return p.MainModule?.FileName ?? ""; } catch { return ""; }
    }

    static bool IsCrossFireName(string value)
        => CrossFireNames.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    static bool IsPublicIPv4(string value)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || b[0] >= 224) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
        return true;
    }

    static void Publish(Form form, List<Candidate> candidates)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        form.BeginInvoke((Action)(() =>
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = form.GetType();
                var listObj = type.GetField("connections", flags)?.GetValue(form);
                if (listObj is not System.Collections.IList list) return;
                var itemType = listObj.GetType().GetGenericArguments().FirstOrDefault();
                if (itemType == null) return;

                list.Clear();
                foreach (var c in candidates)
                {
                    var item = Activator.CreateInstance(itemType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null,
                        args: new object[] { c.Ip, c.Port, "TCP", c.State },
                        culture: null);
                    if (item != null) list.Add(item);
                }

                if (type.GetField("connectionText", flags)?.GetValue(form) is Label connectionText)
                {
                    connectionText.Text = candidates.Count == 0
                        ? "No public CrossFire room endpoint visible yet."
                        : string.Join("\r\n", candidates.Take(8).Select(c => $"TCP  {c.Ip}:{c.Port,-5}  {c.State,-15}  PID {c.Pid}"));
                }

                if (candidates.Count > 0)
                {
                    Log(form, $"[CROSSFIRE] {candidates.Count} room/master TCP candidate(s) visible across the CrossFire process family.");
                    var active = candidates.Where(c => c.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (active.Count > 1)
                        Log(form, "[CROSSFIRE] Multiple ESTABLISHED TCP endpoints detected; endpoint ranking is now allowed to compare them instead of locking to the master socket.");
                }
            }
            catch (Exception ex) { Log(form, "[CROSSFIRE] UI publish error: " + ex.Message); }
        }));
    }

    static async Task<string> RunAsync(string file, string args, int timeoutMs)
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
            if (p == null) return "";
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch { try { p.Kill(true); } catch { } }
            return await output.ConfigureAwait(false) + "\r\n" + await error.ConfigureAwait(false);
        }
        catch { return ""; }
    }

    static void Log(Form form, string text)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        try { form.BeginInvoke((Action)(() => form.GetType().GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }

    readonly record struct TcpEndpoint(string Ip, int Port, int Pid, string ProcessName, string State);
    readonly record struct Candidate(string Ip, int Port, int Pid, string ProcessName, string State, DateTime LastSeenUtc);
    readonly record struct SeenEndpoint(string Ip, int Port, int Pid, string ProcessName, string State, DateTime LastSeenUtc);
}
