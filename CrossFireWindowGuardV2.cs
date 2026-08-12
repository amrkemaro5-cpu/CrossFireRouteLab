using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CrossFireRouteLab;

/// <summary>
/// Hardens the dashboard against CrossFire fullscreen/Alt+Tab minimize races.
/// It never activates the dashboard and never makes it TopMost. When Windows
/// attempts to minimize or hide the dashboard while CrossFire is running, the
/// transition is swallowed or reversed without activation.
/// </summary>
internal sealed class CrossFireWindowGuardV2 : IDisposable
{
    const int WmSysCommand = 0x0112;
    const int WmSize = 0x0005;
    const int WmShowWindow = 0x0018;
    const int WmWindowPosChanging = 0x0046;
    const int WmApp = 0x8000;
    const int WmRestoreNoActivate = WmApp + 0x4A;
    const int ScMinimize = 0xF020;
    const int SizeMinimized = 1;
    const int SwShowNoActivate = 4;
    const uint SwpNoActivate = 0x0010;
    const uint SwpShowWindow = 0x0040;
    const uint SwpNoMove = 0x0002;
    const uint SwpNoSize = 0x0001;
    const uint SwpNoZOrder = 0x0004;
    const uint SwpHideWindow = 0x0080;

    static readonly string[] Names = { "crossfire", "crossfire_x64", "crossfire64", "crossfireclient", "crossfireclient64" };
    readonly Form dashboard;
    readonly GuardNativeWindow native;
    readonly System.Windows.Forms.Timer timer;
    bool disposed;
    bool restoreQueued;

    [DllImport("user32.dll")]
    static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    public CrossFireWindowGuardV2(Form dashboard)
    {
        this.dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        native = new GuardNativeWindow(this);
        dashboard.HandleCreated += Attach;
        dashboard.HandleDestroyed += Detach;
        if (dashboard.IsHandleCreated) Attach(this, EventArgs.Empty);
        timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) => Poll();
        timer.Start();
    }

    static bool CrossFireRunning()
    {
        foreach (var n in Names)
        {
            try
            {
                using var p = Process.GetProcessesByName(n).FirstOrDefault();
                if (p != null) return true;
            }
            catch { }
        }
        return false;
    }

    void Attach(object? s, EventArgs e)
    {
        if (!disposed && dashboard.IsHandleCreated) { try { native.AssignHandle(dashboard.Handle); } catch { } }
    }

    void Detach(object? s, EventArgs e) { try { native.ReleaseHandle(); } catch { } }

    void Poll()
    {
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated || !CrossFireRunning()) return;
        if (IsIconic(dashboard.Handle) || !IsWindowVisible(dashboard.Handle)) QueueRestore();
    }

    void QueueRestore()
    {
        if (restoreQueued || disposed || !dashboard.IsHandleCreated) return;
        restoreQueued = true;
        try { if (!PostMessage(dashboard.Handle, WmRestoreNoActivate, IntPtr.Zero, IntPtr.Zero)) restoreQueued = false; }
        catch { restoreQueued = false; }
    }

    void RestoreNoActivate()
    {
        restoreQueued = false;
        if (disposed || dashboard.IsDisposed || !dashboard.IsHandleCreated || !CrossFireRunning()) return;
        try
        {
            ShowWindowAsync(dashboard.Handle, SwShowNoActivate);
            SetWindowPos(dashboard.Handle, IntPtr.Zero, 0, 0, 0, 0, SwpNoActivate | SwpShowWindow | SwpNoMove | SwpNoSize | SwpNoZOrder);
        }
        catch { }
    }

    internal bool IsGuarding => !disposed && CrossFireRunning();
    internal void Restore() => RestoreNoActivate();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop(); timer.Dispose();
        try { native.ReleaseHandle(); } catch { }
    }

    sealed class GuardNativeWindow : NativeWindow
    {
        readonly CrossFireWindowGuardV2 owner;
        public GuardNativeWindow(CrossFireWindowGuardV2 owner) => this.owner = owner;

        protected override void WndProc(ref Message m)
        {
            if (!owner.disposed && owner.IsGuarding)
            {
                if (m.Msg == WmSysCommand && ((long)m.WParam & 0xFFF0) == ScMinimize)
                {
                    owner.QueueRestore();
                    return;
                }
                if (m.Msg == WmSize && m.WParam.ToInt32() == SizeMinimized)
                {
                    owner.QueueRestore();
                    return;
                }
                if (m.Msg == WmShowWindow && m.WParam == IntPtr.Zero)
                {
                    owner.QueueRestore();
                    return;
                }
                if (m.Msg == WmWindowPosChanging && m.LParam != IntPtr.Zero)
                {
                    try
                    {
                        var pos = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
                        if ((pos.flags & SwpHideWindow) != 0)
                        {
                            pos.flags &= ~SwpHideWindow;
                            Marshal.StructureToPtr(pos, m.LParam, false);
                            return;
                        }
                    }
                    catch { owner.QueueRestore(); }
                }
            }

            if (m.Msg == WmRestoreNoActivate)
            {
                owner.RestoreNoActivate();
                return;
            }
            base.WndProc(ref m);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x, y, cx, cy;
            public uint flags;
        }
    }
}
