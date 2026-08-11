using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// Keeps Game Route Lab available when CrossFire's fullscreen/window-manager
/// behavior minimizes unrelated top-level windows during Alt+Tab.
/// It never activates Game Route Lab, never makes it TopMost, and never
/// changes CrossFire or Windows networking settings.
/// </summary>
internal sealed class CrossFireWindowGuard : IDisposable
{
    const int SwShowNoActivate = 4;
    const int TimerIntervalMs = 100;

    readonly Form dashboard;
    readonly System.Windows.Forms.Timer timer;
    bool disposed;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    public CrossFireWindowGuard(Form dashboard)
    {
        this.dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        timer = new System.Windows.Forms.Timer { Interval = TimerIntervalMs };
        timer.Tick += (_, _) => CheckWindowState();
        timer.Start();
        dashboard.HandleDestroyed += (_, _) => Dispose();
    }

    void CheckWindowState()
    {
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated)
            return;

        // Only intervene while CrossFire is actually the foreground application.
        // This prevents normal user minimization from being immediately undone.
        if (!IsCrossFireForeground())
            return;

        if (dashboard.WindowState == FormWindowState.Minimized)
        {
            // Restore without activation so CrossFire remains in front and keeps
            // keyboard/mouse focus. This is specifically for the Alt+Tab/fullscreen
            // interaction reported with CrossFire.
            ShowWindowAsync(dashboard.Handle, SwShowNoActivate);
        }
    }

    static bool IsCrossFireForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
                return false;

            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (name.Contains("crossfire", StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                var executablePath = process.MainModule?.FileName;
                return !string.IsNullOrEmpty(executablePath) &&
                       executablePath.Contains("CrossFire", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        timer.Stop();
        timer.Dispose();
    }
}
