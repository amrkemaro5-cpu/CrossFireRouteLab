using System.Net;
using System.Reflection;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// CrossFire TCP-only AI route decision layer.
/// The only accepted target is the live TCP endpoint exposed by the TCP probe.
/// UDP is not a supported transport in the decision path.
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
            catch (Exception ex) { Log(form, "[AI ROUTE ENGINE] stopped safely: " + ex.Message); }
            finally { running = false; }
        };

        Log(form, "[AI ROUTE ENGINE] TCP-only mode armed: detect the live CrossFire TCP room/server socket → measure TCP RTT → optimize that exact TCP target.");
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
        Log(form, "[AI ROUTE ENGINE] TCP-only analysis started — waiting for the live CrossFire TCP socket.");
        string ip = "";
        int port = 0;
        string protocol = "";

        for (int i = 0; i < 48 && !form.IsDisposed; i++)
        {
            if (CrossFireRoomTransportProbeV3.TryGetTarget(out ip, out port, out protocol)) break;
            await Task.Delay(500).ConfigureAwait(true);
        }

        if (!IPAddressValid(ip) || port <= 0 || !protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
        {
            Log(form, "[AI ROUTE ENGINE] No live CrossFire TCP room/server socket was observed yet. Stay inside the active match and retry AUTO ANALYZE.");
            return;
        }

        PublishDecision(form, ip, port);
        var optimize = typeof(CrossFireRoomRouteOptimizerV2).GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic);
        if (optimize == null)
        {
            Log(form, "[AI ROUTE ENGINE] TCP route optimizer entry point was not found; target detection completed without changing routing.");
            return;
        }

        Log(form, $"[AI ROUTE ENGINE] TCP target locked to {ip}:{port} from the live CrossFire socket. No UDP target is accepted.");
        var task = optimize.Invoke(null, new object[] { form, ip, port, "TCP" }) as Task;
        if (task != null) await task.ConfigureAwait(true);
        Log(form, $"[AI ROUTE ENGINE] TCP route optimization complete for {ip}:{port}.");
    }

    static void PublishDecision(GameRouteLabV10Form form, string ip, int port)
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
                metrics.Text = $"ENDPOINT   {ip}:{port}\r\nPROTOCOL   TCP\r\nSOURCE     LIVE CROSSFIRE TCP SOCKET\r\nSTATUS     ACTUAL TCP TARGET\r\nUDP        REMOVED";
            }
            if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
            {
                quality.Text = $"● ACTUAL CROSSFIRE TCP • {ip}:{port}";
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
                if (control is Label label && (label.Text.Contains("TCP + UDP", StringComparison.OrdinalIgnoreCase) || label.Text.Contains("TCP/UDP", StringComparison.OrdinalIgnoreCase)))
                    label.Text = "Game Route Lab v10.0  •  CROSSFIRE TCP-ONLY ROUTING";
                if (control is Label guide && guide.Text.Contains("AUTO ANALYZE", StringComparison.OrdinalIgnoreCase) && guide.Text.Contains("UDP", StringComparison.OrdinalIgnoreCase))
                    guide.Text = "Press AUTO ANALYZE — TCP-only CrossFire room/server route pass runs automatically.";
            }
        }
        catch { }
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

    static bool IsCrossFire(GameRouteLabV10Form form)
    {
        var field = typeof(GameRouteLabV10Form).GetField("gameName", BindingFlags.Instance | BindingFlags.NonPublic);
        return (field?.GetValue(form)?.ToString() ?? "").Contains("crossfire", StringComparison.OrdinalIgnoreCase);
    }

    static bool IPAddressValid(string value) => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip);
    static void Log(GameRouteLabV10Form form, string text) { try { typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }); } catch { } }
}
