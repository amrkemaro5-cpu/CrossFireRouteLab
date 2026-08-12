namespace CrossFireRouteLab;

/// <summary>
/// Final runtime visual layer. Keeps the dashboard centered at different window
/// sizes and makes placeholder telemetry visibly animate until real measurements
/// replace it. No networking or Windows settings are changed.
/// </summary>
public sealed partial class DashboardForm
{
    readonly System.Windows.Forms.Timer runtimeVisualTimer = new() { Interval = 50 };
    bool runtimeVisualsReady;

    void InstallRuntimeFixes()
    {
        if (runtimeVisualsReady || IsDisposed) return;
        runtimeVisualsReady = true;

        runtimeVisualTimer.Tick += (_, _) => RuntimeVisualTick();
        runtimeVisualTimer.Start();
        FormClosed += (_, _) =>
        {
            runtimeVisualTimer.Stop();
            runtimeVisualTimer.Dispose();
        };

        ArrangeRuntimeLayout();
        RuntimeVisualTick();
    }

    void RuntimeVisualTick()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            ArrangeRuntimeLayout();

            // The graph is always alive. Once a real endpoint is selected,
            // SampleLivePing replaces this animated placeholder with measurements.
            if (string.IsNullOrWhiteSpace(liveTarget))
            {
                var values = Enumerable.Range(0, 28)
                    .Select(i => 55d + 12d * Math.Sin(i * .58d + phase * 2.2d) + 3d * Math.Sin(i * 1.17d + phase))
                    .ToList();
                graph.Values = values;
            }
            graph.Phase = phase;
            graph.Invalidate();

            foreach (var overlay in telemetryOverlays)
            {
                overlay.Phase = phase;
                // Keep the animation visible without covering the telemetry text.
                overlay.Height = 22;
                overlay.Invalidate();
            }

            UpdateRightTelemetry();
        }
        catch (Exception ex)
        {
            Log("[VISUAL LOOP] " + ex.Message);
        }
    }

    void ArrangeRuntimeLayout()
    {
        var root = Controls.OfType<TableLayoutPanel>().FirstOrDefault();
        if (root == null || root.Controls.Count < 3) return;
        if (root.Controls[2] is not TableLayoutPanel body || body.Controls.Count < 3) return;
        if (body.Controls[1] is not TableLayoutPanel center || center.Controls.Count < 4) return;

        var available = Math.Max(1, center.ClientSize.Height);
        // Percent rows prevent the old fixed 205/205/210 layout from pushing the
        // console and Best Endpoint metrics outside the visible center column.
        center.RowStyles[0].SizeType = SizeType.Percent;
        center.RowStyles[1].SizeType = SizeType.Percent;
        center.RowStyles[2].SizeType = SizeType.Percent;
        center.RowStyles[3].SizeType = SizeType.Percent;
        center.RowStyles[0].Height = available < 560 ? 24 : 25;
        center.RowStyles[1].Height = 24;
        center.RowStyles[2].Height = 27;
        center.RowStyles[3].Height = 24;

        // Keep the center column visually balanced against the side columns.
        body.ColumnStyles[0].Width = body.ClientSize.Width < 1200 ? 236 : 262;
        body.ColumnStyles[2].Width = body.ClientSize.Width < 1200 ? 292 : 318;
    }
}
