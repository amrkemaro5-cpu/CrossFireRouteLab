using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Single CrossFire route entry point.
/// CrossFire uses one live TCP socket detector and one TCP route optimizer.
/// The generic V10 connection and ping paths are isolated whenever CrossFire is running.
/// </summary>
internal static class CrossFireTcpOnlyFinalPatch
{
    static bool armed;
    static System.Threading.Timer? guardTimer;
    static readonly Dictionary<string, Delegate?> originalHandlers = new(StringComparer.OrdinalIgnoreCase);

    public static void Apply(GameRouteLabV10Form form)
    {
        if (armed || form.IsDisposed) return;
        armed = true;
        CrossFireRoomTransportProbeV3.Apply(form);
        InstallButtonGuards(form);
        guardTimer = new System.Threading.Timer(_ => GuardTick(form), null, 0, 500);
        form.FormClosed += (_, _) => { try { guardTimer?.Dispose(); } catch { } guardTimer = null; };
        Log(form, "[CROSSFIRE TCP] Single TCP route path armed: live socket -> TCP measurement -> route optimizer.");
    }

    static void GuardTick(GameRouteLabV10Form form)
    {
        if (form.IsDisposed) return;
        using var process = FindCrossFireProcess();
        if (process == null) return;
        StopGenericTimers(form);
        SetField(form, "gamePid", process.Id);
        SetField(form, "gameName", process.ProcessName);
    }

    static void InstallButtonGuards(GameRouteLabV10Form form)
    {
        InstallGuard(form, "AUTO ANALYZE", async (button, e) =>
        {
            if (IsCrossFireRunning(form)) await AnalyzeCrossFire(form);
            else await InvokeOriginalAsync("AUTO ANALYZE", button, e);
        });

        InstallGuard(form, "FIND CONNECTIONS", async (button, e) =>
        {
            if (IsCrossFireRunning(form)) await RefreshCrossFireTarget(form);
            else await InvokeOriginalAsync("FIND CONNECTIONS", button, e);
        });

        InstallGuard(form, "PING 30x", async (button, e) =>
        {
            if (IsCrossFireRunning(form)) await MeasureCrossFire(form, 30);
            else await InvokeOriginalAsync("PING 30x", button, e);
        });

        InstallGuard(form, "PATH QUALITY", async (button, e) =>
        {
            if (IsCrossFireRunning(form)) await MeasureCrossFire(form, 8);
            else await InvokeOriginalAsync("PATH QUALITY", button, e);
        });
    }

    static void InstallGuard(GameRouteLabV10Form form, string text, Func<Button, EventArgs, Task> replacement)
    {
        var button = FindButton(form, text);
        if (button == null) return;
        try
        {
            var eventsProperty = typeof(Component).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic);
            var eventList = eventsProperty?.GetValue(button) as EventHandlerList;
            var clickKey = typeof(Control).GetField("EventClick", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            if (eventList == null || clickKey == null) return;
            originalHandlers[text] = eventList[clickKey];
            eventList[clickKey] = null;
            button.Click += async (_, e) =>
            {
                try { await replacement(button, e); }
                catch (Exception ex) { Log(form, $"[{text}] {ex.Message}"); }
            };
        }
        catch (Exception ex) { Log(form, $"[CROSSFIRE TCP] Could not isolate {text}: {ex.Message}"); }
    }

    static async Task InvokeOriginalAsync(string name, Button button, EventArgs e)
    {
        if (originalHandlers.TryGetValue(name, out var handler) && handler != null)
            handler.DynamicInvoke(button, e);
        await Task.CompletedTask;
    }

    static async Task AnalyzeCrossFire(GameRouteLabV10Form form)
    {
        StopGenericTimers(form);
        var process = FindCrossFireProcess();
        if (process == null)
        {
            Log(form, "[CROSSFIRE TCP] CrossFire process was not found.");
            return;
        }

        try
        {
            SetField(form, "gamePid", process.Id);
            SetField(form, "gameName", process.ProcessName);
            SetField(form, "endpoint", null);
            SetField(form, "endpointPort", 0);
            SetField(form, "lastPing", -1d);
            SetField(form, "jitter", 0d);
            SetLabel(form, "gameTitle", "CrossFire");
            SetLabel(form, "gameMeta", $"PID       {process.Id}\r\nPATH      CrossFire process\r\nTRANSPORT TCP ONLY");
            SetTextBox(form, "endpointBox", "");
            SetLabel(form, "quality", "● CROSSFIRE TCP • WAITING FOR LIVE SOCKET", Color.FromArgb(40, 242, 122));
            SetLabel(form, "metrics", "ENDPOINT   —\r\nPROTOCOL   TCP\r\nSOURCE     LIVE CROSSFIRE TCP SOCKET\r\nSTATUS     WAITING");

            Log(form, "[CROSSFIRE TCP] Active match detection started. Waiting for a public TCP socket owned by CrossFire.");
            await RefreshCrossFireTarget(form);
            if (CrossFireRoomTransportProbeV3.TryGetTarget(out var ip, out var port, out var protocol) && protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
                await RunOptimizer(form, ip, port);
        }
        finally
        {
            process.Dispose();
        }
    }

    static async Task RefreshCrossFireTarget(GameRouteLabV10Form form)
    {
        StopGenericTimers(form);
        for (var i = 0; i < 48 && !form.IsDisposed; i++)
        {
            if (CrossFireRoomTransportProbeV3.TryGetTarget(out var ip, out var port, out var protocol) &&
                protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
            {
                PublishTarget(form, ip, port);
                return;
            }
            await Task.Delay(500).ConfigureAwait(true);
        }
        Log(form, "[CROSSFIRE TCP] No live public TCP endpoint was observed. Stay inside the active match and retry.");
    }

    static async Task RunOptimizer(GameRouteLabV10Form form, string ip, int port)
    {
        var type = typeof(CrossFireRoomRouteOptimizerV2);
        var optimize = type.GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic);
        if (optimize == null)
        {
            Log(form, "[CROSSFIRE TCP] TCP route optimizer entry point is unavailable.");
            return;
        }

        type.GetField("lastTarget", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, $"{ip}:{port}/TCP");
        type.GetField("lastRun", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, DateTime.UtcNow);
        Log(form, $"[CROSSFIRE TCP] Live target locked to {ip}:{port}. Starting TCP-only route optimization.");
        if (optimize.Invoke(null, new object[] { form, ip, port, "TCP" }) is Task task)
            await task.ConfigureAwait(true);
    }

    static async Task MeasureCrossFire(GameRouteLabV10Form form, int count)
    {
        StopGenericTimers(form);
        if (!CrossFireRoomTransportProbeV3.TryGetTarget(out var ip, out var port, out var protocol) || !protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
        {
            Log(form, "[CROSSFIRE TCP] No live TCP target is available yet.");
            return;
        }

        var values = await TcpSamples(ip, port, count);
        if (values.Count == 0)
        {
            Log(form, $"[CROSSFIRE TCP] {count} TCP samples: no successful connections.");
            return;
        }

        var ordered = values.OrderBy(x => x).ToList();
        var median = ordered[ordered.Count / 2];
        var min = ordered[0];
        var max = ordered[^1];
        var average = ordered.Average();
        var jitter = Math.Sqrt(ordered.Sum(x => Math.Pow(x - average, 2)) / ordered.Count);
        SetField(form, "lastPing", median);
        SetField(form, "jitter", jitter);
        SetLabel(form, "metrics", $"ENDPOINT   {ip}:{port}\r\nPROTOCOL   TCP\r\nTCP RTT    {median:0.0} ms\r\nSAMPLES    {values.Count}/{count}\r\nMIN/MAX   {min:0.0} / {max:0.0} ms\r\nJITTER     {jitter:0.0} ms\r\nSOURCE     LIVE CROSSFIRE TCP SOCKET");
        SetLabel(form, "quality", $"● CROSSFIRE TCP • {median:0} ms • {values.Count}/{count} samples", Color.FromArgb(40, 242, 122));
        Log(form, $"[CROSSFIRE TCP] {count} fresh TCP samples to {ip}:{port}: median {median:0.0} ms, min {min:0.0}, max {max:0.0}, jitter {jitter:0.0}.");
    }

    static async Task<List<double>> TcpSamples(string ip, int port, int count)
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
            await Task.Delay(80).ConfigureAwait(false);
        }
        return values;
    }

    static void StopGenericTimers(GameRouteLabV10Form form)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try { (typeof(GameRouteLabV10Form).GetField("scanTimer", flags)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop(); } catch { }
        try { (typeof(GameRouteLabV10Form).GetField("pingTimer", flags)?.GetValue(form) as System.Windows.Forms.Timer)?.Stop(); } catch { }
    }

    static void PublishTarget(GameRouteLabV10Form form, string ip, int port)
    {
        SetField(form, "endpoint", ip);
        SetField(form, "endpointPort", port);
        SetTextBox(form, "endpointBox", $"{ip}:{port}");
        SetLabel(form, "metrics", $"ENDPOINT   {ip}:{port}\r\nPROTOCOL   TCP\r\nSOURCE     LIVE CROSSFIRE TCP SOCKET\r\nSTATUS     ACTUAL TCP TARGET");
        SetLabel(form, "quality", $"● CROSSFIRE TCP • {ip}:{port}", Color.FromArgb(40, 242, 122));
        Log(form, $"[CROSSFIRE TCP] Target selected from the live CrossFire TCP socket: {ip}:{port}.");
    }

    static Process? FindCrossFireProcess()
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

    static bool IsCrossFireRunning(GameRouteLabV10Form form)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var name = typeof(GameRouteLabV10Form).GetField("gameName", flags)?.GetValue(form)?.ToString() ?? "";
        if (name.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return true;
        using var process = FindCrossFireProcess();
        return process != null;
    }

    static Button? FindButton(Control root, string text) => AllControls(root).OfType<Button>().FirstOrDefault(x => x.Text.Equals(text, StringComparison.OrdinalIgnoreCase));

    static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in AllControls(child)) yield return nested;
        }
    }

    static void SetField(GameRouteLabV10Form form, string name, object? value)
    {
        try { typeof(GameRouteLabV10Form).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(form, value); } catch { }
    }

    static void SetTextBox(GameRouteLabV10Form form, string name, string value)
    {
        try { if (typeof(GameRouteLabV10Form).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) is TextBox box) box.Text = value; } catch { }
    }

    static void SetLabel(GameRouteLabV10Form form, string name, string value, Color? color = null)
    {
        try
        {
            if (typeof(GameRouteLabV10Form).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(form) is Label label)
            {
                label.Text = value;
                if (color.HasValue) label.ForeColor = color.Value;
            }
        }
        catch { }
    }

    static void Log(GameRouteLabV10Form form, string text)
    {
        try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }
}
