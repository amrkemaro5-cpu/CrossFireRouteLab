namespace CrossFireRouteLab;

// Reference-layout pass: keeps the dashboard aligned to the approved neon GRL composition
// while leaving the analyzer/network logic untouched.
public sealed partial class DashboardForm
{
    void ApplyReferenceLayout()
    {
        SuspendLayout();
        try
        {
            ClientSize = new Size(1536, 900);
            MinimumSize = new Size(1220, 800);

            var root = Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            if (root == null || root.Controls.Count < 4) return;

            root.RowStyles[0].Height = 154;
            root.RowStyles[1].Height = 88;
            root.RowStyles[3].Height = 38;

            var header = root.Controls[0];
            var toolbar = root.Controls[1];
            var body = root.Controls[2] as TableLayoutPanel;
            if (body == null) return;

            body.Padding = new Padding(16, 10, 16, 8);
            body.ColumnStyles[0].SizeType = SizeType.Absolute;
            body.ColumnStyles[0].Width = 278;
            body.ColumnStyles[2].SizeType = SizeType.Absolute;
            body.ColumnStyles[2].Width = 338;

            PolishHeader(header);
            PolishToolbar(toolbar);
            PolishCenter(body.Controls[1]);
            PolishSidePanels(body.Controls[0], body.Controls[2]);
            AddReferenceAnimation(header, toolbar, body);
        }
        finally
        {
            ResumeLayout(true);
            PerformLayout();
        }
    }

    static Label? FindLabel(Control root, string text)
        => root.Controls.Cast<Control>().SelectMany(c => c is Label l && l.Text == text ? new[] { l } : FindLabels(c, text)).FirstOrDefault();

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
        var logo = header.Controls.OfType<PictureBox>().FirstOrDefault();
        if (logo != null)
        {
            logo.Image?.Dispose();
            logo.Image = Brand.CreateLogo(144);
            logo.Bounds = new Rectangle(30, 5, 146, 146);
            logo.BackColor = Color.Transparent;
            logo.SizeMode = PictureBoxSizeMode.Zoom;
        }

        var title = FindLabel(header, "GAME ROUTE LAB");
        if (title != null)
        {
            title.Bounds = new Rectangle(184, 30, 700, 42);
            title.Font = new Font("Segoe UI Semibold", 31, FontStyle.Bold);
        }

        var slogan = FindLabel(header, "SMARTER ROUTES.  BETTER PING.");
        if (slogan != null)
        {
            slogan.Bounds = new Rectangle(188, 72, 650, 24);
            slogan.Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold);
        }

        var subtitle = FindLabel(header, "LOCAL-FIRST GAME NETWORK ANALYZER");
        if (subtitle != null)
        {
            subtitle.Bounds = new Rectangle(189, 99, 650, 20);
            subtitle.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        }

        var status = header.Controls.OfType<GRLStatus>().FirstOrDefault();
        if (status != null)
            status.Bounds = new Rectangle(Math.Max(850, header.ClientSize.Width - 278), 24, 250, 70);
    }

    void PolishToolbar(Control toolbar)
    {
        var flow = toolbar.Controls.OfType<FlowLayoutPanel>().FirstOrDefault();
        if (flow == null) return;

        toolbar.Padding = new Padding(12, 6, 12, 5);
        flow.Padding = new Padding(4, 0, 4, 0);
        flow.WrapContents = false;
        flow.AutoScroll = true;
        flow.AutoSize = false;
        flow.FlowDirection = FlowDirection.LeftToRight;

        foreach (Control c in flow.Controls)
        {
            if (c is Label l && l.Text == "ENDPOINT")
            {
                l.Width = 70;
                l.Height = 76;
                l.Margin = new Padding(0, 0, 4, 0);
                l.TextAlign = ContentAlignment.MiddleLeft;
                l.AutoEllipsis = false;
            }
            else if (c is TextBox t)
            {
                t.Width = 162;
                t.Height = 34;
                t.Margin = new Padding(0, 20, 8, 0);
                t.Font = new Font("Segoe UI", 9.2f);
            }
            else if (c is GRLActionButton b)
            {
                b.Width = 104;
                b.Height = 76;
                b.Margin = new Padding(3, 0, 3, 0);
            }
        }
    }

    void PolishCenter(Control centerControl)
    {
        if (centerControl is not TableLayoutPanel center) return;
        center.Margin = Padding.Empty;
        center.RowStyles[0].Height = 30;
        center.RowStyles[1].Height = 27;
        center.RowStyles[2].Height = 25;
        center.RowStyles[3].Height = 18;

        var hero = center.Controls.OfType<GRLCard>().FirstOrDefault();
        if (hero != null) hero.Margin = new Padding(0, 0, 0, 6);

        if (center.Controls.Count > 1 && center.Controls[1] is GRLCard summary)
            summary.Margin = new Padding(0, 0, 0, 6);

        if (center.Controls.Count > 2 && center.Controls[2] is TableLayoutPanel bestRow)
            bestRow.Margin = new Padding(0, 0, 0, 6);
    }

    void PolishSidePanels(Control? left, Control? right)
    {
        if (left is TableLayoutPanel leftLayout)
        {
            leftLayout.RowStyles[1].Height = 154;
            leftLayout.Padding = Padding.Empty;
        }

        if (right is TableLayoutPanel rightLayout)
        {
            rightLayout.RowStyles[0].Height = 31;
            rightLayout.RowStyles[1].Height = 34;
            rightLayout.RowStyles[2].Height = 35;
        }
    }

    void AddReferenceAnimation(Control header, Control toolbar, TableLayoutPanel body)
    {
        animationTimer.Tick += (_, _) =>
        {
            var pulse = (float)((Math.Sin(phase * 1.35f) + 1.0) * .5);
            analysisTitle.ForeColor = Blend(Purple, Cyan, pulse * .32f);
            systemText.ForeColor = Blend(Cyan, Green, pulse * .35f);
            header.Invalidate();
            toolbar.Invalidate();
            body.Invalidate();
            games.Invalidate();
        };
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
