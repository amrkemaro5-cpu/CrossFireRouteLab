using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Single CrossFire route entry point.
/// CrossFire is handled through one live TCP socket detector and the TCP route optimizer.
/// The generic V10 connection/ping timers are disabled while CrossFire is selected so they
/// cannot replace the CrossFire target or measurement.
/// </summary>
internal static class CrossFireTcpOnlyFinalPatch
{
    static bool armed;
    static Delegate? originalAutoAnalyzeClick;

    public static void Apply(GameRouteLabV10Form form)
    {
        if (armed || form.IsDisposed) return;
        armed = true;
        CrossFireRoomTransportProbeV3.Apply(form);
        InstallCrossFireAutoAnalyze(form);
        Log(form, "[CROSSFIRE TCP] Single TCP route path armed: live socket -> TCP measurement -> route optimizer.");
    }

    static void InstallCrossFireAutoAnalyze(GameRouteLabV10Form form)
    {
        var button = FindButton(form, "AUTO ANALYZE");
        if (button == null)
        {
            Log(form, "[CROSSFIRE TCP] AUTO ANALYZE button was not found; live TCP detection remains available.");
            return;
        }

        try
        {
            var eventsProperty = typeof(Component).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic);
            var eventList = eventsProperty?.GetValue(button) as EventHandlerList;
            var clickKey = typeof(Control).GetField("EventClick", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            if (eventList != null && clickKey != null)
            {
                originalAutoAnalyzeClick = eventList[clickKey];
                eventList[clickKey] = null;
            }
        }
        catch (Exception ex)
        {
            Log(form, "[CROSSFIRE TCP] Could not isolate the generic AUTO ANALYZE handler: " + ex.Message);
            return;
        }

        button.Click += async (_, e) =>
        {
            if (!IsCrossFireRunning(form))
            {
                try { originalAutoAnalyzeClick?.DynamicInvoke(button, e); }
                catch (Exception ex) { Log(form, "[AUTO ANALYZE] " + ex.Message); }
                return;
            }

            await AnalyzeCrossFire(form);
        };
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
            SetLabel(form, "gameTitle", PrettyName(process.ProcessName));
            SetLabel(form, "gameMeta", $"PID       {process.Id}\r\nPATH      CrossFire process\r\nTRANSPORT TCP ONLY");
            SetTextBox(form, "endpointBox", "");
            SetLabel(form, "quality", "● CROSSFIRE TCP • WAITING FOR LIVE SOCKET", Color.FromArgb(40, 242, 122));
            SetLabel(form, "metrics", "ENDPOINT   —\r\nPROTOCOL   TCP\r\nSOURCE     LIVE CROSSFIRE TCP SOCKET\r\nSTATUS     WAITING");

            Log(form, "[CROSSFIRE TCP] Active match detection started. Waiting for a public TCP socket owned by CrossFire.");
            for (var i = 0; i < 48 && !form.IsDisposed; i++)
            {
                if (CrossFireRoomTransportProbeV3.TryGetTarget(out var ip, out var port, out var protocol) &&
                    protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
                {
                    PublishTarget(form, ip, port);
                    await RunOptimizer(form, ip, port);
                    return;
                }
                await Task.Delay(500).ConfigureAwait(true);
            }

            Log(form, "[CROSSFIRE TCP] No live public TCP endpoint was observed. Stay inside the active match and run AUTO ANALYZE again.");
        }
        finally
        {
            process.Dispose();
        }
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

        // Prevent the optimizer's background timer from starting the same target a second time.
        type.GetField("lastTarget", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, $"{ip}:{port}/TCP");
        type.GetField("lastRun", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, DateTime.UtcNow);

        Log(form, $"[CROSSFIRE TCP] Live target locked to {ip}:{port}. Starting TCP-only route optimization.");
        if (optimize.Invoke(null, new object[] { form, ip, port, "TCP" }) is Task task)
            await task.ConfigureAwait(true);
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

    static string PrettyName(string processName) => processName.Contains("crossfire", StringComparison.OrdinalIgnoreCase) ? "CrossFire" : processName;

    static void Log(GameRouteLabV10Form form, string text)
    {
        try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }
}
