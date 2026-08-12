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

            // v10 deliberately does not install a CrossFire window guard and
            // never changes WindowState, activation, focus, or TopMost status.
            var dashboard = new GameRouteLabV10Form();
            BindTelemetryText(dashboard);
            Application.Run(dashboard);
        }
        catch (Exception ex)
        {
            CrashReporter.Show(ex, "Startup");
        }
    }

    static void BindTelemetryText(GameRouteLabV10Form form)
    {
        // Keep the telemetry controls visually layered inside their animated
        // cards without adding another UI timer or another layout engine.
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(GameRouteLabV10Form);
        var networkPanel = (Control?)type.GetField("networkPanel", flags)?.GetValue(form);
        var routerPanel = (Control?)type.GetField("routerPanel", flags)?.GetValue(form);
        var networkText = (Control?)type.GetField("networkText", flags)?.GetValue(form);
        var routerText = (Control?)type.GetField("routerText", flags)?.GetValue(form);
        if (networkPanel != null && networkText != null) networkPanel.Controls.Add(networkText);
        if (routerPanel != null && routerText != null) routerPanel.Controls.Add(routerText);
    }
}

internal static class CrashReporter
{
    static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GameRouteLab");

    public static void Write(Exception ex, string source)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, "startup-error.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\r\n{ex}\r\n\r\n");
        }
        catch { }
    }

    public static void Show(Exception ex, string source)
    {
        Write(ex, source);
        try
        {
            MessageBox.Show(
                "Game Route Lab could not start correctly.\r\n\r\n" +
                "A diagnostic log was saved to:\r\n" +
                Path.Combine(Root, "startup-error.log") + "\r\n\r\n" +
                "Error: " + ex.Message,
                "Game Route Lab — Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch { }
    }
}
