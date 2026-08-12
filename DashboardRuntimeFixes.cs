namespace CrossFireRouteLab;

/// <summary>
/// Lightweight runtime visual layer. Layout is calculated only when the window
/// actually changes size; the visual timer only updates paint-only animation.
/// This avoids forcing TableLayoutPanel layout/reflow dozens of times per second.
/// </summary>
public sealed partial class DashboardForm
{
    // This timer is deliberately slow: telemetry decoration is not a frame loop.
    readonly System.Windows.Forms.Timer runtimeVisualTimer = new() { Interval = 180 };
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

        // Layout once after the controls have their real client sizes.
        ArrangeRuntimeLayout();
        RuntimeVisualTick();
    }

    void RuntimeVisualTick()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            // IMPORTANT: do not call ArrangeRuntimeLayout() here.
            // Layout is expensive and runs on the WinForms UI thread.
            // The resize handlers in DashboardPolish.cs already keep the layout
            // correct when the user actually resizes the window.
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
                overlay.Height = 22;
                overlay.Invalidate();
            }

            UpdateRightTelemetry();
        }
        catch (Exception ex)
        {
            // Never let a decorative animation fault interrupt the UI loop.
            System.Diagnostics.Debug.WriteLine("[VISUAL LOOP] " + ex.Message);
        }
    }

    void ArrangeRuntimeLayout()
    {
        var root = Controls.OfType<TableLayoutPanel>().FirstOrDefault();
        if (root == null || root.Controls.Count < 3) return;
        if (root.Controls[2] is not TableLayoutPanel body || body.Controls.Count < 3) return;
        if (body.Controls[1] is not TableLayoutPanel center || center.Controls.Count < 4) return;

        var available = Math.Max(1, center.ClientSize.Height);
        center.SuspendLayout();
        body.SuspendLayout();
        try
        {
            center.RowStyles[0].SizeType = SizeType.Percent;
            center.RowStyles[1].SizeType = SizeType.Percent;
            center.RowStyles[2].SizeType = SizeType.Percent;
            center.RowStyles[3].SizeType = SizeType.Percent;
            center.RowStyles[0].Height = available < 560 ? 24 : 25;
            center.RowStyles[1].Height = 24;
            center.RowStyles[2].Height = 27;
            center.RowStyles[3].Height = available < 560 ? 25 : 24;

            body.ColumnStyles[0].Width = body.ClientSize.Width < 1200 ? 236 : 262;
            body.ColumnStyles[2].Width = body.ClientSize.Width < 1200 ? 292 : 318;

            ArrangeBestRuntime(center.Controls[2]);
            ArrangeSummaryRuntime(center.Controls[1]);
            ArrangeHeroRuntime(center.Controls[0]);
        }
        finally
        {
            body.ResumeLayout(true);
            center.ResumeLayout(true);
        }
    }

    void ArrangeBestRuntime(Control control)
    {
        if (control is not GRLCard card) return;
        var w = Math.Max(1, card.ClientSize.Width);
        var h = Math.Max(1, card.ClientSize.Height);
        var leftWidth = Math.Max(340, (int)(w * .46));
        var graphX = Math.Min(leftWidth + 26, Math.Max(320, w / 2));
        var graphWidth = Math.Max(260, w - graphX - 18);

        var title = card.Controls.Cast<Control>().FirstOrDefault(c => c is Label l && l.Text == "BEST ENDPOINT (CURRENT)");
        title?.SetBounds(18, 10, Math.Max(300, leftWidth - 20), 28);
        best.SetBounds(20, 43, leftWidth, 38);
        metrics.SetBounds(20, 84, leftWidth, Math.Max(76, h - 94));
        quality.SetBounds(graphX, 10, graphWidth, 30);
        quality.TextAlign = ContentAlignment.TopRight;
        graph.SetBounds(graphX, 48, graphWidth, Math.Max(92, h - 58));
        graph.BackColor = Surface;
    }

    void ArrangeSummaryRuntime(Control control)
    {
        if (control is not GRLCard card) return;
        var w = Math.Max(1, card.ClientSize.Width);
        var h = Math.Max(1, card.ClientSize.Height);
        var split = Math.Max(420, w / 2);
        detectedGameIcon.Bounds = new Rectangle(20, 50, 58, 58);
        gameName.SetBounds(92, 50, Math.Max(260, split - 108), 34);
        gameMeta.SetBounds(92, 88, Math.Max(260, split - 108), Math.Max(52, h - 96));
        connections.SetBounds(split + 8, 50, Math.Max(260, w - split - 26), Math.Max(70, h - 62));
    }

    void ArrangeHeroRuntime(Control control)
    {
        if (control is not GRLCard card) return;
        var w = Math.Max(1, card.ClientSize.Width);
        var h = Math.Max(1, card.ClientSize.Height);
        var radarSize = Math.Min(116, Math.Max(94, h - 48));
        radar.Bounds = new Rectangle(18, Math.Max(18, (h - radarSize) / 2), radarSize, radarSize);
        var left = radar.Right + 18;
        var right = Math.Max(left + 240, w - 22);
        analysisTitle.SetBounds(left, 16, Math.Max(240, right - left), 34);
        var sub = card.Controls.Cast<Control>().FirstOrDefault(c => c is Label l && l.Text.StartsWith("Detecting the game"));
        sub?.SetBounds(left, 52, Math.Max(240, right - left), 24);
        progress.SetBounds(left, 84, Math.Max(200, right - left - 62), 14);
        progressText.SetBounds(right - 52, 78, 52, 24);
    }
}
