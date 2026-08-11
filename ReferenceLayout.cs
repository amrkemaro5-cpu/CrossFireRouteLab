namespace CrossFireRouteLab;

// Stable reference layout pass. This pass is intentionally deterministic:
// it uses the same geometry rules at startup and on resize so controls do not
// drift, clip, or become misaligned at common Windows DPI settings.
public sealed partial class DashboardForm
{
    void ApplyReferenceLayout()
    {
        SuspendLayout();
        try
        {
            ClientSize = new Size(1536, 900);
            MinimumSize = new Size(1180, 760);
            AutoScaleMode = AutoScaleMode.Dpi;

            var root = Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            if (root == null || root.Controls.Count < 4) return;

            root.Dock = DockStyle.Fill;
            root.Margin = Padding.Empty;
            root.Padding = Padding.Empty;
            root.RowStyles[0].SizeType = SizeType.Absolute;
            root.RowStyles[0].Height = 142;
            root.RowStyles[1].SizeType = SizeType.Absolute;
            root.RowStyles[1].Height = 82;
            root.RowStyles[2].SizeType = SizeType.Percent;
            root.RowStyles[2].Height = 100;
            root.RowStyles[3].SizeType = SizeType.Absolute;
            root.RowStyles[3].Height = 36;

            var header = root.Controls[0];
            var toolbar = root.Controls[1];
            var body = root.Controls[2] as TableLayoutPanel;
            if (body == null) return;

            body.Dock = DockStyle.Fill;
            body.Margin = Padding.Empty;
            body.Padding = new Padding(14, 8, 14, 7);
            body.ColumnStyles[0].SizeType = SizeType.Absolute;
            body.ColumnStyles[0].Width = 262;
            body.ColumnStyles[1].SizeType = SizeType.Percent;
            body.ColumnStyles[1].Width = 100;
            body.ColumnStyles[2].SizeType = SizeType.Absolute;
            body.ColumnStyles[2].Width = 318;

            PolishHeader(header);
            PolishToolbar(toolbar);
            PolishCenter(body.Controls[1]);
            PolishConsole(body.Controls[1]);
            PolishSidePanels(body.Controls[0], body.Controls[2]);
            AddReferenceAnimation(header, toolbar, body);

            root.Resize -= RootResize;
            root.Resize += RootResize;
        }
        finally
        {
            ResumeLayout(true);
            PerformLayout();
        }
    }

    void RootResize(object? sender, EventArgs e)
    {
        if (sender is not TableLayoutPanel root || root.Controls.Count < 3) return;
        var body = root.Controls[2] as TableLayoutPanel;
        if (body == null) return;

        var available = Math.Max(0, body.ClientSize.Width - body.Padding.Horizontal);
        var left = available < 1000 ? 235 : 262;
        var right = available < 1000 ? 292 : 318;
        body.ColumnStyles[0].Width = left;
        body.ColumnStyles[2].Width = right;
    }

    static Label? FindLabel(Control root, string text)
        => root.Controls.Cast<Control>()
            .SelectMany(c => c is Label l && l.Text == text ? new[] { l } : FindLabels(c, text))
            .FirstOrDefault();

    static IEnumerable<Label> FindLabels(Control root, string text)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Label l && l.Text == text) yield return l;
            foreach (var nested in FindLabels(c, text)) yield return nested;
        }
    }

    void PolishHeader(Control header)
    {
        header.Dock = DockStyle.Fill;

        var logo = header.Controls.OfType<PictureBox>().FirstOrDefault();
        if (logo != null)
        {
            logo.Image?.Dispose();
            logo.Image = Brand.CreateLogo(150);
            logo.Bounds = new Rectangle(28, 4, 150, 132);
            logo.BackColor = Color.Transparent;
            logo.SizeMode = PictureBoxSizeMode.Zoom;
        }

        var title = FindLabel(header, "GAME ROUTE LAB");
        if (title != null)
        {
            title.Bounds = new Rectangle(178, 25, 720, 42);
            title.Font = new Font("Segoe UI Semibold", 30, FontStyle.Bold);
            title.AutoEllipsis = false;
        }

        var slogan = FindLabel(header, "SMARTER ROUTES.  BETTER PING.");
        if (slogan != null)
        {
            slogan.Bounds = new Rectangle(182, 67, 660, 23);
            slogan.Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold);
        }

        var subtitle = FindLabel(header, "LOCAL-FIRST GAME NETWORK ANALYZER");
        if (subtitle != null)
        {
            subtitle.Bounds = new Rectangle(183, 94, 680, 20);
            subtitle.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        }

        var status = header.Controls.OfType<GRLStatus>().FirstOrDefault();
        if (status != null)
            status.Bounds = new Rectangle(Math.Max(760, header.ClientSize.Width - 278), 22, 250, 70);
    }

    void PolishToolbar(Control toolbar)
    {
        toolbar.Dock = DockStyle.Fill;
        toolbar.Padding = new Padding(12, 5, 12, 5);

        var flow = toolbar.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
        if (flow == null) return;

        flow.Dock = DockStyle.Fill;
        flow.Padding = new Padding(2, 0, 2, 0);
        flow.Margin = Padding.Empty;
        flow.WrapContents = false;
        flow.AutoScroll = true;
        flow.HorizontalScroll.Enabled = true;
        flow.VerticalScroll.Enabled = false;
        flow.AutoSize = false;
        flow.FlowDirection = FlowDirection.LeftToRight;

        foreach (Control c in flow.Controls)
        {
            if (c is Label l && l.Text == "ENDPOINT")
            {
                l.Width = 72;
                l.Height = 72;
                l.Margin = new Padding(0, 0, 5, 0);
                l.TextAlign = ContentAlignment.MiddleLeft;
                l.AutoEllipsis = false;
            }
            else if (c is TextBox t)
            {
                t.Width = 176;
                t.Height = 34;
                t.Margin = new Padding(0, 19, 9, 0);
                t.Font = new Font("Segoe UI", 9.2f);
                t.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            }
            else if (c is GRLActionButton b)
            {
                b.Width = 104;
                b.Height = 72;
                b.Margin = new Padding(3, 0, 3, 0);
            }
        }
    }

    void PolishCenter(Control centerControl)
    {
        if (centerControl is not TableLayoutPanel center) return;

        center.Dock = DockStyle.Fill;
        center.Margin = Padding.Empty;
        center.Padding = Padding.Empty;

        // Percent rows are used here deliberately. Absolute rows caused the
        // console to collapse to zero height on small CI/DPI logical sizes.
        center.RowStyles[0].SizeType = SizeType.Percent;
        center.RowStyles[0].Height = 28;
        center.RowStyles[1].SizeType = SizeType.Percent;
        center.RowStyles[1].Height = 22;
        center.RowStyles[2].SizeType = SizeType.Percent;
        center.RowStyles[2].Height = 22;
        center.RowStyles[3].SizeType = SizeType.Percent;
        center.RowStyles[3].Height = 28;

        if (center.Controls.Count > 0 && center.Controls[0] is GRLCard hero)
            hero.Margin = new Padding(0, 0, 0, 5);
        if (center.Controls.Count > 1 && center.Controls[1] is GRLCard summary)
            summary.Margin = new Padding(0, 0, 0, 5);
        if (center.Controls.Count > 2 && center.Controls[2] is TableLayoutPanel bestRow)
            bestRow.Margin = new Padding(0, 0, 0, 5);
        if (center.Controls.Count > 3 && center.Controls[3] is GRLCard consoleCard)
            consoleCard.Margin = Padding.Empty;
    }

    void PolishConsole(Control centerControl)
    {
        if (centerControl is not TableLayoutPanel center || center.Controls.Count < 4) return;
        if (center.Controls[3] is not GRLCard card) return;

        card.Padding = new Padding(10, 34, 10, 10);
        console.Dock = DockStyle.Fill;
        console.Margin = Padding.Empty;
        console.Location = Point.Empty;
        console.Size = Size.Empty;
        console.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        console.BackColor = Color.FromArgb(1, 4, 10);
        console.ForeColor = TextColor;
        console.BorderStyle = BorderStyle.FixedSingle;
        console.Font = new Font("Cascadia Mono", 8.8f);
        console.ReadOnly = true;
        console.WordWrap = false;
        console.ScrollBars = RichTextBoxScrollBars.Both;
        console.HideSelection = false;
        console.DetectUrls = false;
        console.BringToFront();

        var header = card.Controls.OfType<Label>().FirstOrDefault(x => x.Text == "LIVE ANALYSIS CONSOLE");
        if (header != null)
        {
            header.Location = new Point(10, 7);
            header.Size = new Size(Math.Max(220, card.ClientSize.Width - 20), 22);
            header.BringToFront();
        }
    }

    void PolishSidePanels(Control? left, Control? right)
    {
        if (left is TableLayoutPanel leftLayout)
        {
            leftLayout.Dock = DockStyle.Fill;
            leftLayout.Padding = Padding.Empty;
            leftLayout.RowStyles[1].SizeType = SizeType.Absolute;
            leftLayout.RowStyles[1].Height = 154;
        }

        if (right is TableLayoutPanel rightLayout)
        {
            rightLayout.Dock = DockStyle.Fill;
            rightLayout.Padding = Padding.Empty;
            rightLayout.RowStyles[0].SizeType = SizeType.Percent;
            rightLayout.RowStyles[0].Height = 31;
            rightLayout.RowStyles[1].SizeType = SizeType.Percent;
            rightLayout.RowStyles[1].Height = 34;
            rightLayout.RowStyles[2].SizeType = SizeType.Percent;
            rightLayout.RowStyles[2].Height = 35;
        }
    }

    void AddReferenceAnimation(Control header, Control toolbar, TableLayoutPanel body)
    {
        animationTimer.Tick -= ReferenceAnimationTick;
        animationTimer.Tick += ReferenceAnimationTick;
    }

    void ReferenceAnimationTick(object? sender, EventArgs e)
    {
        var pulse = (float)((Math.Sin(phase * 1.35f) + 1.0) * .5);
        analysisTitle.ForeColor = Blend(Purple, Cyan, pulse * .30f);
        systemText.ForeColor = Blend(Cyan, Green, pulse * .32f);
        games.Invalidate();
    }

    static Color Blend(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            a.R + (int)((b.R - a.R) * amount),
            a.G + (int)((b.G - a.G) * amount),
            a.B + (int)((b.B - a.B) * amount));
    }
}
