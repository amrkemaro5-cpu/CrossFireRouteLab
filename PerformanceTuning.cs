namespace CrossFireRouteLab;

/// <summary>
/// Keeps decorative animation smooth without turning the UI thread into a 30+ Hz
/// layout/render workload. Network/process work is handled separately by the
/// scanner's background path.
/// </summary>
public sealed partial class DashboardForm
{
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 66 ms is ~15 FPS for decorative chrome, which is enough for a radar,
        // progress shimmer and sparkline while substantially reducing UI work.
        animationTimer.Interval = 66;

        // These flags help custom-painted dashboard surfaces avoid unnecessary
        // background erase/flicker work. All controls remain on the UI thread.
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        UpdateStyles();
    }
}
