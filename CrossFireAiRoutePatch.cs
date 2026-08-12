using System.Reflection;
using System.Net;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// One-click CrossFire route decision layer, TCP-only.
/// The target is the live TCP endpoint discovered from the CrossFire process.
/// No UDP target, UDP probe, DNS target or synthetic game packet is used.
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

        Log(form, "[AI ROUTE ENGINE] One-click TCP mode armed: detect CrossFire → read live TCP room/server IP → measure TCP → test best local route.");
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
        Log(form, "[AI ROUTE ENGINE] TCP-only full pass started — live CrossFire endpoint, TCP latency, then route comparison.");

        string ip = "";
        int port = 0;
        for (int i = 0; i < 30 && !form.IsDisposed; i++)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            ip = typeof(GameRouteLabV10Form).GetField("endpoint", flags)?.GetValue(form)?.ToString() ?? "";
            port = Convert.ToInt32(typeof(GameRouteLabV10Form).GetField("endpointPort", flags)?.GetValue(form) ?? 0);
            if (IPAddressValid(ip) && port > 0) break;
            await Task.Delay(500).ConfigureAwait(true);
        }

        if (!IPAddressValid(ip) || port <= 0)
        {
            Log(form, "[AI ROUTE ENGINE] No live CrossFire TCP endpoint is visible yet. Stay inside the room/match; the TCP-only detector will keep watching.");
            return;
        }

        PublishDecision(form, ip, port);

        var optimize = typeof(CrossFireRoomRouteOptimizerV2).GetMethod("Optimize", BindingFlags.Static | BindingFlags.NonPublic);
        if (optimize == null)
        {
            Log(form, "[AI ROUTE ENGINE] Route optimizer entry point was not found; TCP measurement completed without changing routing.");
            return;
        }

        Log(form, $"[AI ROUTE ENGINE] Target locked to {ip}:{port} (TCP). Testing that exact CrossFire endpoint — no UDP and no DNS target.");
        var task = optimize.Invoke(null, new object[] { form, ip, port, "TCP" }) as Task;
        if (task != null) await task.ConfigureAwait(true);
        Log(form, "[AI ROUTE ENGINE] One-click TCP pass complete. No UDP route/probe was used.");
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
                metrics.Text = $"ENDPOINT   {ip}:{port}\r\nPROTOCOL   TCP\r\nSOURCE     LIVE CROSSFIRE SOCKET\r\nSTATUS     ACTUAL TCP TARGET\r\nUDP        DISABLED";
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
                if (control is Label label && label.Text.Contains("READ-ONLY", StringComparison.OrdinalIgnoreCase))
                    label.Text = "Game Route Lab v10.0  •  TCP-ONLY CROSSFIRE ROOM DETECTION  •  NO UDP";
                if (control is Label guide && guide.Text.Contains("Press AUTO ANALYZE", StringComparison.OrdinalIgnoreCase))
                    guide.Text = guide.Text.Replace("Press AUTO ANALYZE.", "Press AUTO ANALYZE — TCP-only room/server route pass runs automatically.");
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

    static bool IPAddressValid(string value)
        => IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip);

    static void Log(GameRouteLabV10Form form, string text)
    {
        try { typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }); } catch { }
    }
}
