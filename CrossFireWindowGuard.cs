using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// Protects the GRL dashboard from the CrossFire exclusive-fullscreen / Alt+Tab
/// minimize race without stealing focus from the game.
///
/// The previous guard called ShowWindow/SetWindowPos directly from several
/// window messages. That could create a restore/minimize feedback loop on some
/// DirectX fullscreen transitions. v11 only cancels an external minimize when
/// CrossFire is the foreground application, and otherwise posts a deferred
/// restore message outside the original WndProc call stack.
/// </summary>
internal sealed class CrossFireWindowGuard : IDisposable
{
    const int SwShowNoActivate = 4;
    const int TimerIntervalMs = 350;

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

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

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

    static bool IsCrossFireForeground()
    {
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return false;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0) return false;
            using var process = Process.GetProcessById((int)pid);
            return CrossFireProcessNames.Any(n => process.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    void CheckWindowState()
    {
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated) return;
        if (!IsCrossFireRunning()) return;
        if (!IsCrossFireForeground()) return;
        if (!IsIconic(dashboard.Handle) && IsWindowVisible(dashboard.Handle)) return;
        PostRestore();
    }

    void PostRestore()
    {
        if (disposed || restorePosted || !dashboard.IsHandleCreated) return;
        restorePosted = true;
        try { PostMessage(dashboard.Handle, WmRestoreNoActivate, IntPtr.Zero, IntPtr.Zero); }
        catch { restorePosted = false; }
    }

    void RestoreWithoutActivation()
    {
        restorePosted = false;
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated) return;
        if (!IsCrossFireRunning()) return;

        try
        {
            // SW_SHOWNOACTIVATE restores the dashboard while leaving CrossFire
            // as the active application. No TopMost and no z-order promotion.
            ShowWindowAsync(dashboard.Handle, SwShowNoActivate);
            NativeWindow.UpdateWindowRegion(dashboard.Handle, SwpNoActivate | SwpShowWindow | SwpNoMove | SwpNoSize | SwpNoZOrder);
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
                // If CrossFire is the active foreground app, a minimize command
                // arriving for GRL is an external fullscreen/focus transition.
                // Cancel it instead of recursively restoring inside the message.
                if (m.Msg == WmSysCommand && ((long)m.WParam & 0xFFF0) == ScMinimize && IsCrossFireForeground())
                {
                    owner.PostRestore();
                    return;
                }

                if (m.Msg == WmSize && m.WParam.ToInt32() == SizeMinimized && IsCrossFireForeground())
                {
                    owner.PostRestore();
                    return;
                }

                if (m.Msg == WmShowWindow && m.WParam == IntPtr.Zero && IsCrossFireForeground())
                {
                    owner.PostRestore();
                    return;
                }

                if (m.Msg == WmWindowPosChanging && m.LParam != IntPtr.Zero && IsCrossFireForeground())
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

                if (m.Msg == WmRestoreNoActivate)
                {
                    owner.RestoreWithoutActivation();
                    return;
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
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    }
}
