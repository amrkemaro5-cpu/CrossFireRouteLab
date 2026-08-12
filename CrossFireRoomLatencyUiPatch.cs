using System.Reflection;

namespace CrossFireRouteLab;

/// <summary>
/// Prevents the generic one-second ICMP/TCP probe from overwriting the
/// CrossFire room measurement. The room panel is driven by V3 passive data.
/// </summary>
internal static class CrossFireRoomLatencyUiPatch
{
    static System.Threading.Timer? timer;
    static string lastLogged = "";

    public static void Apply(GameRouteLabV10Form form)
    {
        timer?.Dispose();
        timer = new System.Threading.Timer(_ => Tick(form), null, 500, 500);
        form.FormClosed += (_, _) => { try { timer?.Dispose(); } catch { } timer = null; };
        Log(form, "[CROSSFIRE] Generic live probe display is disabled for CrossFire; room RTT comes from passive capture.");
    }

    static void Tick(GameRouteLabV10Form form)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            var type = typeof(GameRouteLabV10Form);
            if (type.GetField("gameName", flags)?.GetValue(form)?.ToString() is not string game || !game.Contains("crossfire", StringComparison.OrdinalIgnoreCase)) return;
            if (type.GetField("pingTimer", flags)?.GetValue(form) is System.Windows.Forms.Timer ping) ping.Stop();
            if (!CrossFireRoomTransportProbeV3.TryGetTarget(out var ip, out var port, out var protocol)) return;
            CrossFireRoomTransportProbeV3.TryGetPassiveRtt(out var rtt, out var samples, out _);
            form.BeginInvoke((Action)(() =>
            {
                try
                {
                    if (type.GetField("endpoint", flags) != null) type.GetField("endpoint", flags)!.SetValue(form, ip);
                    if (type.GetField("endpointPort", flags) != null) type.GetField("endpointPort", flags)!.SetValue(form, port);
                    if (type.GetField("endpointBox", flags)?.GetValue(form) is TextBox box) box.Text = $"{ip}:{port}";
                    if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                    {
                        metrics.Text = rtt >= 0
                            ? $"ENDPOINT   {ip}:{port}\r\nPROTOCOL   {protocol}\r\nLATENCY    {rtt:0} ms PASSIVE RTT\r\nSAMPLES    {samples}\r\nSOURCE     ACTUAL ROOM TRAFFIC\r\nSTATUS     {(rtt < 80 ? "GOOD" : rtt < 120 ? "HIGH" : "HIGH LATENCY")}" 
                            : $"ENDPOINT   {ip}:{port}\r\nPROTOCOL   {protocol}\r\nLATENCY    — ms\r\nSOURCE     ACTUAL ROOM TRAFFIC\r\nSTATUS     PASSIVE RTT PENDING";
                        metrics.ForeColor = Color.FromArgb(40, 242, 122);
                    }
                    if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                    {
                        quality.Text = rtt >= 0 ? $"● ACTUAL ROOM • {rtt:0} ms PASSIVE RTT" : $"● ACTUAL ROOM • {protocol} • RTT PENDING";
                        quality.ForeColor = Color.FromArgb(40, 242, 122);
                    }
                }
                catch { }
            }));
            string state = rtt >= 0 ? $"{ip}:{port}/{protocol} RTT {rtt:0} ms" : $"{ip}:{port}/{protocol} room flow";
            if (!state.Equals(lastLogged, StringComparison.Ordinal)) { lastLogged = state; Log(form, "[CROSSFIRE] " + state); }
        }
        catch { }
    }

    static void Log(GameRouteLabV10Form form, string text)
    { try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { } }
}
