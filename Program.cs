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

            // v8 deliberately does NOT attach CrossFireWindowGuard. The old guard
            // could interfere with Alt+Tab/fullscreen CrossFire window behavior.
            Application.Run(new GameRouteLabV8Form());
        }
        catch (Exception ex)
        {
            CrashReporter.Show(ex, "Startup");
        }
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
