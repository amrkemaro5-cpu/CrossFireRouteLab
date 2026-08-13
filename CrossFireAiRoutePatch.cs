using System.Net;
using System.Reflection;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// One-click CrossFire route decision layer.
/// The target is always the transport endpoint published by
/// CrossFireRoomTransportProbeV3. TCP/UDP are both supported; no legacy
/// endpoint list is allowed to override the V3 room target.
/// </summary>
internal static class CrossFireAiRoutePatch
{
    static bool armed;
    static bool running;

    public static void Apply(GameRouteLabV10Form form)
    {
        if (armed || form.IsDisposed) return;
        armed = true;
        UpdateUi(form);

        var button = FindButton(form, "AUTO ANALYZE");
        if (button == null)
        {
            Log(form, "[AI ROUTE ENGINE] AUTO ANALYZE button not found.");
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
            finally { running = false; }
        };

        Log(form, "[AI ROUTE ENGINE] One-click transport-aware mode armed: detect CrossFire → wait for V3 actual room TCP/UDP target → benchmark that exact target.");
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
        Log(form, "[AI ROUTE ENGINE] Full transport-aware pass started — waiting for the V3 passive room detector; no synthetic TCP/UDP probes.");

        string ip = "";
        int port = 0;
        string protocol = "";

        // IMPORTANT: never read endpoint/endpointPort first. Older patches can
        // publish CrossFire control sockets such as TCP/10009. V3 is the sole
        // authority for the actual room target.
        for (int i = 0; i < 48 && !form.IsDisposed; i++)
        {
            if (CrossFireRoomTransportProbeV3.TryGetTarget(out ip, out port, out protocol)) break;
            await Task.Delay(500).ConfigureAwait(true);
        }

        if (!IPAddressValid(ip) || port <= 0 || !IsSupportedProtocol(protocol))
        {
            Log(form, "[AI ROUTE ENGINE] No V3-verified CrossFire room transport was observed yet. Stay inside the active room/match; the passive TCP/UDP detector will keep watching.");
            return;
        }

        PublishDecision(form, ip, port, protocol);

        var optimize = typeof(CrossFireRoomRouteOptimizerV2).GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic);
        if (optimize == null)
        {
            Log(form, "[AI ROUTE ENGINE] Route optimizer entry point was not found; target detection completed without changing routing.");
            return;
        }

        Log(form, $"[AI ROUTE ENGINE] Target locked to {ip}:{port} ({protocol}) from V3 actual-room detection. TCP 10009/13008/16666 are never substituted.");
        var task = optimize.Invoke(null, new object[] { form, ip, port, protocol }) as Task;
        if (task != null) await task.ConfigureAwait(true);
        Log(form, $"[AI ROUTE ENGINE] One-click {protocol} route pass complete. Target came from passive CrossFire room traffic.");
    }

    static void PublishDecision(GameRouteLabV10Form form, string ip, int port, string protocol)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var type = typeof(GameRouteLabV10Form);
            type.GetField("endpoint", flags)?.SetValue(form, ip);
            type.GetField("endpointPort", flags)?.SetValue(form, port);
            if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{ip}:{port}";
            if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
            {
                metrics.Text = protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)
                    ? $"ENDPOINT   {ip}:{port}\r\nPROTOCOL   TCP\r\nSOURCE     V3 PASSIVE ROOM FLOW\r\nSTATUS     ACTUAL TCP ROOM TARGET\r\nUDP        AVAILABLE"
                    : $"ENDPOINT   {ip}:{port}\r\nPROTOCOL   UDP\r\nSOURCE     V3 PASSIVE ROOM FLOW\r\nSTATUS     ACTUAL UDP ROOM TARGET\r\nTCP        CONTROL/OTHER ONLY";
            }
            if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
            {
                quality.Text = $"● ACTUAL CROSSFIRE ROOM • {protocol} • {ip}:{port}";
                quality.ForeColor = Color.FromArgb(40, 242, 122);
            }
        }
        catch (Exception ex) { Log(form, "[AI ROUTE ENGINE] UI publish warning: " + ex.Message); }
    }

    static void UpdateUi(GameRouteLabV10Form form)
    {
        try
        {
            foreach (var control in AllControls(form))
            {
                if (control is Label label && label.Text.Contains("READ-ONLY", StringComparison.OrdinalIgnoreCase))
                    label.Text = "Game Route Lab v10.0  •  V3 CROSSFIRE ROOM DETECTION  •  TCP + UDP";
                if (control is Label guide && guide.Text.Contains("Press AUTO ANALYZE", StringComparison.OrdinalIgnoreCase))
                    guide.Text = guide.Text.Replace("Press AUTO ANALYZE — TCP-only room/server route pass runs automatically.", "Press AUTO ANALYZE — V3 room TCP/UDP route pass runs automatically.");
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

    static bool IsSupportedProtocol(string value)
        => value.Equals("TCP", StringComparison.OrdinalIgnoreCase) || value.Equals("UDP", StringComparison.OrdinalIgnoreCase);

    static bool IPAddressValid(string value)
        => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip);

    static void Log(GameRouteLabV10Form form, string text)
    {
        try { typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }); } catch { }
    }
}
