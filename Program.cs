using System.Reflection;
using System.Windows.Forms;

namespace CrossFireRouteLab;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => CrashReporter.Show(e.Exception, "UI thread");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception ex) CrashReporter.Write(ex, "AppDomain");
            };

            // v10 remains the active dashboard and its design/layout are unchanged.
            // The guard prevents CrossFire fullscreen/Alt+Tab transitions from
            // minimizing or hiding the dashboard without stealing game focus.
            var dashboard = new GameRouteLabV10Form();
            BindTelemetryText(dashboard);
            IspTrackerPatch.Apply(dashboard);
            TelemetryVisibilityPatch.Apply(dashboard);
            AutoOptimizationPatch.Apply(dashboard);
            EndpointMeasurementPatch.Apply(dashboard);
            CrossFireConnectionDiscoveryPatch.Apply(dashboard);
            CrossFireRoomTransportPatch.Apply(dashboard);
            RouteOptimizerPatch.Apply(dashboard);

            using var crossFireGuard = new CrossFireWindowGuardV2(dashboard);
            Application.Run(dashboard);
        }
        catch (Exception ex)
        {
            CrashReporter.Show(ex, "Startup");
        }
    }

    static void BindTelemetryText(GameRouteLabV10Form form)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(GameRouteLabV10Form);
        var networkPanel = (Control?)type.GetField("networkPanel", flags)?.GetValue(form);
        var routerPanel = (Control?)type.GetField("routerPanel", flags)?.GetValue(form);
        var networkText = (Control?)type.GetField("networkText", flags)?.GetValue(form);
        var routerText = (Control?)type.GetField("routerText", flags)?.GetValue(form);
        if (networkPanel != null && networkText != null && !networkPanel.Controls.Contains(networkText)) networkPanel.Controls.Add(networkText);
        if (routerPanel != null && routerText != null && !routerPanel.Controls.Contains(routerText)) routerPanel.Controls.Add(routerText);
    }
}

internal static class CrashReporter
{
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab");
    public static void Write(Exception ex, string source)
    {
        try { Directory.CreateDirectory(Root); File.AppendAllText(Path.Combine(Root, "startup-error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\r\n{ex}\r\n\r\n"); } catch { }
    }
    public static void Show(Exception ex, string source)
    {
        Write(ex, source);
        try { MessageBox.Show("Game Route Lab could not start correctly.\r\n\r\nA diagnostic log was saved to:\r\n" + Path.Combine(Root, "startup-error.log") + "\r\n\r\nError: " + ex.Message, "Game Route Lab — Startup Error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
    }
}
