using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// Keeps the GRL dashboard visible while CrossFire changes display mode or
/// focus. The guard deliberately never activates GRL, never makes it TopMost,
/// and never moves it in the z-order. CrossFire remains the active game.
///
/// The important rule is that a CrossFire minimize/hide transition is handled
/// even when the foreground-window query is temporarily inconclusive. During
/// exclusive-fullscreen startup Windows can report the launcher, shell, or
/// another transition window for a few milliseconds; using that query as a
/// gate was the reason earlier guards still missed the race.
/// </summary>
internal sealed class CrossFireWindowGuard : IDisposable
{
    const int SwShowNoActivate = 4;
    const int TimerIntervalMs = 200;

    const int WmSysCommand = 0x0112;
    const int WmSize = 0x0005;
    const int WmShowWindow = 0x0018;
    const int WmWindowPosChanging = 0x0046;
    const int WmApp = 0x8000;
    const int WmRestoreNoActivate = WmApp + 0x3A;

    const int ScMinimize = 0xF020;
    const int SizeMinimized = 1;
    const int SwpNoActivate = 0x0010;
    const int SwpShowWindow = 0x0040;
    const int SwpNoMove = 0x0002;
    const int SwpNoSize = 0x0001;
    const int SwpNoZOrder = 0x0004;
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
    bool restorePosted;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

    static bool IsCrossFireRunning()
    {
        foreach (var name in CrossFireProcessNames)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0) return true;
                }
                finally
                {
                    foreach (var process in processes) process.Dispose();
                }
            }
            catch { }
        }
        return false;
    }

    void CheckWindowState()
    {
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated) return;
        if (!IsCrossFireRunning()) return;

        // Do not require CrossFire to be reported as foreground here. During
        // exclusive-fullscreen transitions that state can be transiently wrong.
        if (!IsIconic(dashboard.Handle) && IsWindowVisible(dashboard.Handle)) return;
        PostRestore();
    }

    void PostRestore()
    {
        if (disposed || restorePosted || !dashboard.IsHandleCreated) return;
        restorePosted = true;
        try
        {
            if (!PostMessage(dashboard.Handle, WmRestoreNoActivate, IntPtr.Zero, IntPtr.Zero))
                restorePosted = false;
        }
        catch { restorePosted = false; }
    }

    void RestoreWithoutActivation()
    {
        restorePosted = false;
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated) return;
        if (!IsCrossFireRunning()) return;

        try
        {
            // Restore without activating GRL. CrossFire keeps keyboard/mouse
            // focus and remains the foreground application.
            ShowWindowAsync(dashboard.Handle, SwShowNoActivate);
            GuardWindow.UpdateWindowRegion(
                dashboard.Handle,
                SwpNoActivate | SwpShowWindow | SwpNoMove | SwpNoSize | SwpNoZOrder);
        }
        catch { }
    }

    public void Dispose()
    {
        if (disposed) return;
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
            if (!owner.disposed && IsCrossFireRunning())
            {
                // These are the exact messages that were being missed during
                // CrossFire's fullscreen/Alt+Tab transition. Do not gate them
                // on GetForegroundWindow(); CrossFire can be in a transient
                // launcher/DirectX transition state at that instant.
                if (m.Msg == WmSysCommand && ((long)m.WParam & 0xFFF0) == ScMinimize)
                {
                    owner.PostRestore();
                    return;
                }

                if (m.Msg == WmSize && m.WParam.ToInt32() == SizeMinimized)
                {
                    owner.PostRestore();
                    return;
                }

                if (m.Msg == WmShowWindow && m.WParam == IntPtr.Zero)
                {
                    owner.PostRestore();
                    return;
                }

                if (m.Msg == WmWindowPosChanging && m.LParam != IntPtr.Zero)
                {
                    try
                    {
                        var pos = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                        if ((pos.flags & SwpHideWindow) != 0)
                        {
                            owner.PostRestore();
                            return;
                        }
                    }
                    catch { }
                }
            }

            if (m.Msg == WmRestoreNoActivate)
            {
                owner.RestoreWithoutActivation();
                return;
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

        internal static void UpdateWindowRegion(IntPtr handle, uint flags)
        {
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, flags);
        }

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
    }
}
