using System.Reflection;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Final CrossFire TCP-only bootstrap layer.
/// The live TCP detector is the single source of truth for the CrossFire
/// endpoint consumed by the AI route engine and route optimizer.
/// </summary>
internal static class CrossFireTcpOnlyFinalPatch
{
    static bool armed;

    public static void Apply(GameRouteLabV10Form form)
    {
        if (armed || form.IsDisposed) return;
        armed = true;

        StopLegacyTimers();
        CrossFireRoomTransportProbeV3.Apply(form);
        SetTcpOnlyUi(form);
        Log(form, "[TCP ONLY] CrossFire route layer armed. The AI consumes the live CrossFire TCP socket and TCP connect RTT only.");
    }

    static void StopLegacyTimers()
    {
        StopTimer("CrossFireRoomTransportPatch");
        StopTimer("CrossFirePacketRoomDiscoveryPatchV2");
        StopTimer("CrossFireSameTransportProbe");
    }

    static void StopTimer(string typeName)
    {
        try
        {
            var type = typeof(Program).Assembly.GetType("CrossFireRouteLab." + typeName);
            var field = type?.GetField("timer", BindingFlags.Static | BindingFlags.NonPublic);
            if (field?.GetValue(null) is IDisposable d) d.Dispose();
            field?.SetValue(null, null);
        }
        catch { }
    }

    static void SetTcpOnlyUi(GameRouteLabV10Form form)
    {
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(GameRouteLabV10Form);
            if (type.GetField("quality", flags)?.GetValue(form) is Label quality)
                quality.Text = "● TCP ONLY • WAITING FOR CROSSFIRE ROOM/SERVER";
            if (type.GetField("metrics", flags)?.GetValue(form) is Label metrics)
                metrics.Text = "ENDPOINT   —\r\nPROTOCOL   TCP\r\nROLE       WAITING FOR LIVE CROSSFIRE SOCKET";
        }
        catch { }
    }

    static void Log(GameRouteLabV10Form form, string text)
    {
        try { form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { text }))); } catch { }
    }
}
