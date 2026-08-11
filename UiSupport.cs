namespace CrossFireRouteLab;

public enum GRLIcon
{
    Radar, Gamepad, Network, Router, Search, Route, Ping, Trace, Chart, Report, Trash
}

public sealed partial class DashboardForm
{
    void AddAction(FlowLayoutPanel flow, GRLIcon icon, string text, Func<Task> action, Color accent)
    {
        var button = new GRLActionButton
        {
            Icon = icon,
            Accent = accent,
            Text = IconGlyph(icon) + Environment.NewLine + text,
            Width = 104,
            Height = 72,
            Margin = new Padding(3, 0, 3, 0),
            TextAlign = ContentAlignment.MiddleCenter,
            FlatStyle = FlatStyle.Flat
        };
        button.Click += async (_, _) => await Safe(action);
        actions.Add(button);
        flow.Controls.Add(button);
    }

    static string IconGlyph(GRLIcon icon) => icon switch
    {
        GRLIcon.Radar => "◎",
        GRLIcon.Gamepad => "⌁",
        GRLIcon.Network => "◇",
        GRLIcon.Router => "▣",
        GRLIcon.Search => "⌕",
        GRLIcon.Route => "⌁",
        GRLIcon.Ping => "◷",
        GRLIcon.Trace => "⌁",
        GRLIcon.Chart => "▥",
        GRLIcon.Report => "▤",
        GRLIcon.Trash => "♲",
        _ => "•"
    };
}

public sealed class GameMemoryItem : Panel
{
    readonly GameProfile profile;
    readonly Color accent;
    readonly Color cyan;
    bool hot;

    public GameMemoryItem(GameProfile profile, Color accent, Color cyan)
    {
        this.profile = profile;
        this.accent = accent;
        this.cyan = cyan;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(5, 12, 24);
        Cursor = Cursors.Hand;
        Margin = new Padding(0, 2, 0, 5);
        SetStyle(ControlStyles.Selectable, true);
        MouseEnter += (_, _) => { hot = true; Invalidate(); };
        MouseLeave += (_, _) => { hot = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var border = hot ? cyan : Color.FromArgb(20, 65, 92);
        using var pen = new Pen(border, hot ? 2 : 1);
        e.Graphics.DrawRoundedRectangle(pen, new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), 10);

        var iconRect = new Rectangle(9, 9, 54, 54);
        using var iconBg = new SolidBrush(Color.FromArgb(10, 22, 38));
        e.Graphics.FillRoundedRectangle(iconBg, iconRect, 8);
        try
        {
            if (!string.IsNullOrWhiteSpace(profile.IconPath) && File.Exists(profile.IconPath))
            {
                using var img = Image.FromFile(profile.IconPath);
                e.Graphics.DrawImage(img, iconRect);
            }
        }
        catch { }

        using var titleBrush = new SolidBrush(Color.FromArgb(235, 244, 255));
        using var metaBrush = new SolidBrush(cyan);
        using var bestBrush = new SolidBrush(accent);
        using var titleFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        using var metaFont = new Font("Segoe UI", 8.2f);
        using var bestFont = new Font("Segoe UI Semibold", 8.2f, FontStyle.Bold);
        e.Graphics.DrawString(profile.DisplayName, titleFont, titleBrush, 72, 10);
        e.Graphics.DrawString($"{profile.Observations} analyses", metaFont, metaBrush, 72, 32);
        e.Graphics.DrawString(string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "No endpoint yet" : "Best: " + profile.LastBestEndpoint, bestFont, bestBrush, 72, 49);
        using var dot = new SolidBrush(accent);
        e.Graphics.FillEllipse(dot, Width - 20, 12, 8, 8);
    }
}
