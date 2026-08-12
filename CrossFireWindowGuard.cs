using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// Keeps the Game Route Lab dashboard from being minimized/hidden during
/// CrossFire startup, fullscreen transitions, and Alt+Tab focus races.
///
/// The guard never activates the dashboard, never makes it TopMost, and never
/// changes networking. When CrossFire is running it only restores the dashboard
/// without taking keyboard/mouse focus away from the game.
/// </summary>
internal sealed class CrossFireWindowGuard : IDisposable
{
    const int SwShowNoActivate = 4;
    const int TimerIntervalMs = 200;

    const int WmSysCommand = 0x0112;
    const int WmSize = 0x0005;
    const int WmShowWindow = 0x0018;
    const int WmWindowPosChanging = 0x0046;

    const int ScMinimize = 0xF020;
    const int SizeMinimized = 1;
    const int SwpNoActivate = 0x0010;
    const int SwpShowWindow = 0x0040;
    const int SwpHideWindow = 0x0080;

    static readonly string[] CrossFireProcessNames =
    {
        "crossfire",
        "crossfire_x64",
        "crossfire64",
        "crossfireclient",
        "crossfireclient64"
    };

    readonly Form dashboard;
    readonly System.Windows.Forms.Timer timer;
    readonly GuardWindow nativeWindow;
    bool disposed;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

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

        // 200 ms is fast enough to catch the launch/fullscreen race without
        // putting a high-frequency process/network scan on the UI thread.
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

    static bool IsCrossFireRunning()
    {
        foreach (var name in CrossFireProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0)
                        return true;
                }
                finally
                {
                    foreach (var process in processes)
                        process.Dispose();
                }
            }
            catch { }
        }

        return false;
    }

    void CheckWindowState()
    {
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated)
            return;

        // Do not interfere with normal user minimization when CrossFire is not
        // running. Once CrossFire starts, protect the dashboard continuously
        // through the short startup/fullscreen/Alt+Tab transition window.
        if (!IsCrossFireRunning())
            return;

        if (!IsIconic(dashboard.Handle) && IsWindowVisible(dashboard.Handle))
            return;

        RestoreWithoutActivation();
    }

    void RestoreWithoutActivation()
    {
        try
        {
            // SHOWNOACTIVATE restores a minimized window without stealing focus
            // from CrossFire. The second call explicitly re-shows the window
            // without changing its z-order or activation state.
            ShowWindowAsync(dashboard.Handle, SwShowNoActivate);

            SetWindowPos(
                dashboard.Handle,
                IntPtr.Zero,
                0,
                0,
                0,
                0,
                SwpNoActivate | SwpShowWindow);
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
            if (!owner.disposed && IsCrossFireRunning())
            {
                // Handle the minimize/hide messages directly. This closes the
                // race where CrossFire changes display mode before the timer has
                // had a chance to run.
                if (m.Msg == WmSysCommand &&
                    ((long)m.WParam & 0xFFF0) == ScMinimize)
                {
                    owner.RestoreWithoutActivation();
                    return;
                }

                if (m.Msg == WmSize && m.WParam.ToInt32() == SizeMinimized)
                {
                    owner.RestoreWithoutActivation();
                    return;
                }

                if (m.Msg == WmShowWindow && m.WParam == IntPtr.Zero)
                {
                    owner.RestoreWithoutActivation();
                    return;
                }

                if (m.Msg == WmWindowPosChanging && m.LParam != IntPtr.Zero)
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
