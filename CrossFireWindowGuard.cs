using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// Protects the dashboard from the minimize/hide side effects some CrossFire
/// fullscreen/Alt+Tab paths send to other top-level windows.
///
/// This guard is deliberately event-driven and lightweight. It never makes
/// Game Route Lab TopMost, never activates it, and never changes networking.
/// It only restores the dashboard when a CrossFire-related focus/minimize race
/// is detected.
/// </summary>
internal sealed class CrossFireWindowGuard : IDisposable
{
    const int SwShowNoActivate = 4;
    const int TimerIntervalMs = 400;
    const int RecentCrossFireWindowMs = 2500;

    const int WmSysCommand = 0x0112;
    const int WmSize = 0x0005;
    const int WmShowWindow = 0x0018;
    const int WmWindowPosChanging = 0x0046;

    const int ScMinimize = 0xF020;
    const int SizeMinimized = 1;
    const int SwpHideWindow = 0x0080;

    readonly Form dashboard;
    readonly System.Windows.Forms.Timer timer;
    readonly GuardWindow nativeWindow;
    bool disposed;
    long lastCrossFireForegroundTick = long.MinValue;

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool IsWindowVisible(IntPtr hWnd);

    public CrossFireWindowGuard(Form dashboard)
    {
        this.dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        nativeWindow = new GuardWindow(this);

        dashboard.HandleCreated += AttachNativeWindow;
        dashboard.HandleDestroyed += DetachNativeWindow;
        if (dashboard.IsHandleCreated)
            AttachNativeWindow(this, EventArgs.Empty);

        // Low-frequency safety net for fullscreen transitions. The normal path
        // is handled directly in WndProc, so this does not poll continuously.
        timer = new System.Windows.Forms.Timer { Interval = TimerIntervalMs };
        timer.Tick += (_, _) => CheckWindowState();
        timer.Start();

        dashboard.HandleDestroyed += (_, _) => Dispose();
    }

    void AttachNativeWindow(object? sender, EventArgs e)
    {
        if (disposed || !dashboard.IsHandleCreated)
            return;

        try { nativeWindow.AssignHandle(dashboard.Handle); }
        catch { }
    }

    void DetachNativeWindow(object? sender, EventArgs e)
    {
        try { nativeWindow.ReleaseHandle(); }
        catch { }
    }

    void ObserveForeground()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
                return;

            using var process = Process.GetProcessById((int)pid);
            if (process.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase))
                lastCrossFireForegroundTick = Environment.TickCount64;
        }
        catch { }
    }

    bool CrossFireWasForegroundRecently()
    {
        ObserveForeground();
        var last = lastCrossFireForegroundTick;
        if (last == long.MinValue)
            return false;

        return Environment.TickCount64 - last <= RecentCrossFireWindowMs;
    }

    void CheckWindowState()
    {
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated)
            return;

        // Keep the dashboard visible through the short focus transition caused
        // by CrossFire entering/leaving fullscreen or Alt+Tabbing.
        if (!IsIconic(dashboard.Handle) && IsWindowVisible(dashboard.Handle))
            return;

        if (!CrossFireWasForegroundRecently())
            return;

        RestoreWithoutActivation();
    }

    void RestoreWithoutActivation()
    {
        try
        {
            // SW_SHOWNOACTIVATE is intentional: CrossFire retains keyboard/mouse
            // focus while Game Route Lab is restored to the desktop.
            ShowWindowAsync(dashboard.Handle, SwShowNoActivate);
        }
        catch { }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        timer.Stop();
        timer.Dispose();
        try { nativeWindow.ReleaseHandle(); }
        catch { }
    }

    sealed class GuardWindow : NativeWindow
    {
        readonly CrossFireWindowGuard owner;

        public GuardWindow(CrossFireWindowGuard owner) => this.owner = owner;

        protected override void WndProc(ref Message m)
        {
            if (!owner.disposed)
            {
                // Record CrossFire as the last foreground application before
                // processing focus/minimize messages. This is important because
                // Alt+Tab can change the foreground window before WM_SIZE arrives.
                owner.ObserveForeground();

                var crossFireTransition = owner.CrossFireWasForegroundRecently();

                if (crossFireTransition && m.Msg == WmSysCommand &&
                    ((long)m.WParam & 0xFFF0) == ScMinimize)
                {
                    owner.RestoreWithoutActivation();
                    return;
                }

                if (crossFireTransition && m.Msg == WmSize &&
                    m.WParam.ToInt32() == SizeMinimized)
                {
                    owner.RestoreWithoutActivation();
                    return;
                }

                if (crossFireTransition && m.Msg == WmShowWindow && m.WParam == IntPtr.Zero)
                {
                    owner.RestoreWithoutActivation();
                    return;
                }

                if (crossFireTransition && m.Msg == WmWindowPosChanging &&
                    m.LParam != IntPtr.Zero)
                {
                    try
                    {
                        var pos = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                        if ((pos.flags & SwpHideWindow) != 0)
                        {
                            owner.RestoreWithoutActivation();
                            return;
                        }
                    }
                    catch { }
                }
            }

            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }
    }
}
