using System.Reflection;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// One-click CrossFire route decision layer.
/// It corrects the transport classification used by the existing room probe,
/// waits for a real in-room target, publishes passive RTT when it is available,
/// and then invokes the room-targeted route optimizer against that exact target.
///
/// This is intentionally an evidence engine, not a fake cloud "AI": it never
/// invents a server, never treats a generic ICMP result as the scoreboard ping,
/// and never changes persistent routes.
/// </summary>
internal static class CrossFireAiRoutePatch
{
    static bool armed;
    static bool running;

    public static void Apply(GameRouteLabV10Form form)
    {
        if (armed || form.IsDisposed) return;
        armed = true;
        FixCrossFirePortClassification();
        UpdateUi(form);

        var button = FindButton(form, "AUTO ANALYZE");
        if (button == null)
        {
            Log(form, "[AI ROUTE ENGINE] Auto Analyze button was not found; automatic room routing remains available through the existing timers.");
            return;
        }

        button.Click += async (_, _) =>
        {
            if (running) return;
            running = true;
            try
            {
                await WaitForGuidedAnalysis(form).ConfigureAwait(true);
                if (!IsCrossFire(form)) return;
                await RunOneClick(form).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log(form, "[AI ROUTE ENGINE] stopped safely: " + ex.Message);
            }
            finally
            {
                running = false;
            }
        };

        Log(form, "[AI ROUTE ENGINE] One-click mode armed: AUTO ANALYZE now continues into actual-room RTT + best-route testing for CrossFire.");
    }

    static void FixCrossFirePortClassification()
    {
        try
        {
            var field = typeof(CrossFireRoomTransportProbeV3).GetField("ControlPorts", BindingFlags.Static | BindingFlags.NonPublic);
            if (field?.GetValue(null) is HashSet<int> ports)
                ports.Remove(10009);
        }
        catch { }
    }

    static async Task WaitForGuidedAnalysis(GameRouteLabV10Form form)
    {
        var busyField = typeof(GameRouteLabV10Form).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic);
        for (int i = 0; i < 120 && !form.IsDisposed; i++)
        {
            if (busyField?.GetValue(form) is not bool busy || !busy) return;
            await Task.Delay(250).ConfigureAwait(true);
        }
    }

    static async Task RunOneClick(GameRouteLabV10Form form)
    {
        Log(form, "[AI ROUTE ENGINE] Full pass started — room transport, passive RTT, then route comparison.");
        Log(form, "[AI ROUTE ENGINE] CrossFire 10009 is treated as a game-server transport candidate, not automatically as control traffic.");

        string ip = "";
        int port = 0;
        string protocol = "";
        double rtt = -1;
        int samples = 0;
        string method = "";

        for (int i = 0; i < 35 && !form.IsDisposed; i++)
        {
            CrossFireRoomTransportProbeV3.TryGetTarget(out ip, out port, out protocol);
            CrossFireRoomTransportProbeV3.TryGetPassiveRtt(out rtt, out samples, out method);
            if (IPAddressValid(ip) && port > 0 && protocol.Length > 0) break;
            await Task.Delay(1000).ConfigureAwait(true);
        }

        if (!IPAddressValid(ip) || port <= 0 || protocol.Length == 0)
        {
            Log(form, "[AI ROUTE ENGINE] No verified public room target arrived. Keep the game inside the active room and run AUTO ANALYZE once more; packet capture also requires Administrator rights.");
            return;
        }

        PublishDecision(form, ip, port, protocol, rtt, samples, method);

        var optimize = typeof(CrossFireRoomRouteOptimizerV2).GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic);
        if (optimize == null)
        {
            Log(form, "[AI ROUTE ENGINE] Route optimizer entry point was not found; measurement was completed without changing routing.");
            return;
        }

        Log(form, $"[AI ROUTE ENGINE] Target locked to {ip}:{port} ({protocol}). Testing the real room target — not DNS, not 8.8.8.8, not a web endpoint.");
        var task = optimize.Invoke(null, new object[] { form, ip, port, protocol }) as Task;
        if (task != null) await task.ConfigureAwait(true);
        Log(form, "[AI ROUTE ENGINE] One-click pass complete. The result shown is the measured best available local route; no ISP route is invented.");
    }

    static void PublishDecision(GameRouteLabV10Form form, string ip, int port, string protocol, double rtt, int samples, string method)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(GameRouteLabV10Form);
        try
        {
            type.GetField("endpoint", flags)?.SetValue(form, ip);
            type.GetField("endpointPort", flags)?.SetValue(form, port);
            if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{ip}:{port}";

            if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
            {
                string latency = rtt >= 0 && samples > 0 ? $"ACTUAL RTT {rtt:0} ms" : "ACTUAL RTT — waiting for safe passive correlation";
                metrics.Text = $"ENDPOINT   {ip}:{port}\r\nPROTOCOL   {protocol}\r\n{latency}\r\nSAMPLES    {(samples > 0 ? samples.ToString() : "—")}\r\nMETHOD     {(method.Length > 0 ? method : "packet flow")}\r\nSTATUS     ACTUAL ROOM TARGET";
            }

            if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
            {
                quality.Text = rtt >= 0 && samples > 0
                    ? $"● ACTUAL ROOM • {rtt:0} ms PASSIVE RTT"
                    : $"● ACTUAL ROOM • {protocol} • PASSIVE RTT PENDING";
                quality.ForeColor = Color.FromArgb(40, 242, 122);
            }

            if (rtt >= 0 && samples > 0)
                Log(form, $"[AI ROUTE ENGINE] Actual room passive RTT = {rtt:0.0} ms from {samples} {method} sample(s). No synthetic game packets were sent.");
            else
                Log(form, "[AI ROUTE ENGINE] Actual room flow is known, but no safe passive RTT correlation was available in this capture; no fake ping value is displayed.");
        }
        catch (Exception ex)
        {
            Log(form, "[AI ROUTE ENGINE] UI publish warning: " + ex.Message);
        }
    }

    static void UpdateUi(GameRouteLabV10Form form)
    {
        try
        {
            foreach (var control in AllControls(form))
            {
                if (control is Label label && label.Text.Contains("READ-ONLY", StringComparison.OrdinalIgnoreCase))
                    label.Text = "Game Route Lab v10.0  •  SAFE ACTIVE-STORE ROUTE TESTS  •  NO DNS/ROUTER CHANGES";
                if (control is Label guide && guide.Text.Contains("Press AUTO ANALYZE", StringComparison.OrdinalIgnoreCase))
                    guide.Text = guide.Text.Replace("Press AUTO ANALYZE.", "Press AUTO ANALYZE — AI route pass runs automatically.");
            }
        }
        catch { }
    }

    static Button? FindButton(Control root, string text)
        => AllControls(root).OfType<Button>().FirstOrDefault(x => x.Text.Equals(text, StringComparison.OrdinalIgnoreCase));

    static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in AllControls(child)) yield return nested;
        }
    }

    static bool IsCrossFire(GameRouteLabV10Form form)
    {
        var field = typeof(GameRouteLabV10Form).GetField("gameName", BindingFlags.Instance | BindingFlags.NonPublic);
        var name = field?.GetValue(form)?.ToString() ?? "";
        return name.Contains("crossfire", StringComparison.OrdinalIgnoreCase);
    }

    static bool IPAddressValid(string value) => System.Net.IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !ip.Equals(System.Net.IPAddress.Loopback);

    static void Log(GameRouteLabV10Form form, string text)
    {
        try { typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }); } catch { }
    }
}
