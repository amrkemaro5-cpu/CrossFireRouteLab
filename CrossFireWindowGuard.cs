using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// Keeps Game Route Lab available when CrossFire/fullscreen window management
/// sends minimize messages to unrelated top-level windows. It never activates
/// Game Route Lab, never makes it TopMost, and never changes networking.
/// </summary>
internal sealed class CrossFireWindowGuard : IDisposable
{
    const int SwShowNoActivate = 4;
    const int SwRestore = 9;
    const int TimerIntervalMs = 150;
    const int WmSysCommand = 0x0112;
    const int WmSize = 0x0005;
    const int ScMinimize = 0xF020;
    const int SizeMinimized = 1;

    readonly Form dashboard;
    readonly System.Windows.Forms.Timer timer;
    readonly GuardWindow nativeWindow;
    bool disposed;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsIconic(IntPtr hWnd);

    public CrossFireWindowGuard(Form dashboard)
    {
        this.dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        nativeWindow = new GuardWindow(this);
        dashboard.HandleCreated += AttachNativeWindow;
        dashboard.HandleDestroyed += DetachNativeWindow;
        if (dashboard.IsHandleCreated) AttachNativeWindow(this, EventArgs.Empty);

        timer = new System.Windows.Forms.Timer { Interval = TimerIntervalMs };
        timer.Tick += (_, _) => CheckWindowState();
        timer.Start();
        dashboard.HandleDestroyed += (_, _) => Dispose();
    }

    void AttachNativeWindow(object? sender, EventArgs e)
    {
        if (disposed || !dashboard.IsHandleCreated) return;
        try { nativeWindow.AssignHandle(dashboard.Handle); } catch { }
    }

    void DetachNativeWindow(object? sender, EventArgs e)
    {
        try { nativeWindow.ReleaseHandle(); } catch { }
    }

    void CheckWindowState()
    {
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated)
            return;

        // CrossFire is the only condition under which we protect the dashboard.
        // Normal user minimization remains untouched when another application is active.
        if (!IsCrossFireForeground())
            return;

        if (IsIconic(dashboard.Handle) || dashboard.WindowState == FormWindowState.Minimized || !IsWindowVisible(dashboard.Handle))
        {
            try
            {
                // Restore without activation so CrossFire keeps keyboard/mouse focus.
                ShowWindowAsync(dashboard.Handle, SwShowNoActivate);
                if (IsIconic(dashboard.Handle))
                    ShowWindowAsync(dashboard.Handle, SwRestore);
                dashboard.BeginInvoke((Action)(() =>
                {
                    if (!dashboard.IsDisposed && IsCrossFireForeground() && dashboard.WindowState == FormWindowState.Minimized)
                        dashboard.WindowState = FormWindowState.Normal;
                }));
            }
            catch { }
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
        try { nativeWindow.ReleaseHandle(); } catch { }
    }

    sealed class GuardWindow : NativeWindow
    {
        readonly CrossFireWindowGuard owner;

        public GuardWindow(CrossFireWindowGuard owner) => this.owner = owner;

        protected override void WndProc(ref Message m)
        {
            if (!owner.disposed && IsCrossFireForeground())
            {
                if (m.Msg == WmSysCommand && ((long)m.WParam & 0xFFF0) == ScMinimize)
                {
                    // CrossFire/window-manager minimize request: swallow it.
                    ShowWindowAsync(owner.dashboard.Handle, SwShowNoActivate);
                    return;
                }

                if (m.Msg == WmSize && m.WParam.ToInt32() == SizeMinimized)
                {
                    // Some fullscreen paths use WM_SIZE rather than WM_SYSCOMMAND.
                    ShowWindowAsync(owner.dashboard.Handle, SwShowNoActivate);
                    return;
                }
            }

            base.WndProc(ref m);
        }
    }
}
