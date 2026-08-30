using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StageBug;

public sealed class MainForm : Form
{
    private readonly Label status = new();
    private readonly Label game = new();
    private readonly Button create = new();
    private readonly Button initialize = new();
    private readonly Button boost1 = new();
    private readonly Button boost2 = new();
    private readonly Timer timer = new() { Interval = 1000 };

    public MainForm()
    {
        Text = "StageBug";
        ClientSize = new Size(338, 365);
        MinimumSize = new Size(338, 365);
        MaximumSize = new Size(338, 365);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(20, 20, 20);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var title = new Label { Text = "StageBug", Dock = DockStyle.Top, Height = 48, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 16F, FontStyle.Bold), ForeColor = Color.White };
        game.Text = "CrossFire: not detected";
        game.AutoSize = false; game.Height = 32; game.Dock = DockStyle.Top; game.TextAlign = ContentAlignment.MiddleCenter;
        status.Text = "Ready";
        status.AutoSize = false; status.Height = 32; status.Dock = DockStyle.Bottom; status.TextAlign = ContentAlignment.MiddleCenter;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24, 20, 24, 20) };
        panel.RowStyles.Clear();
        for (int i = 0; i < 4; i++) panel.RowStyles.Add(new RowStyle(SizeType.Percent, 25));

        Configure(create, "CREATE A ROOM", () => status.Text = "Create Room: client state prepared");
        Configure(initialize, "INITIALIZE SESSION", () => status.Text = "Session initialization requested");
        Configure(boost1, "TRIGGER BOOST 1", () => status.Text = "Boost 1 requested");
        Configure(boost2, "TRIGGER BOOST 2", () => status.Text = "Boost 2 requested");
        panel.Controls.Add(create, 0, 0); panel.Controls.Add(initialize, 0, 1); panel.Controls.Add(boost1, 0, 2); panel.Controls.Add(boost2, 0, 3);

        Controls.Add(panel); Controls.Add(status); Controls.Add(game); Controls.Add(title);
        timer.Tick += (_, _) => UpdateGameState();
        timer.Start();
        UpdateGameState();
    }

    private static void Configure(Button b, string text, Action action)
    {
        b.Text = text; b.Dock = DockStyle.Fill; b.Margin = new Padding(0, 4, 0, 4); b.FlatStyle = FlatStyle.Flat; b.Font = new Font("Segoe UI", 9F, FontStyle.Bold); b.Click += (_, _) => action();
    }

    private void UpdateGameState()
    {
        bool found = Process.GetProcessesByName("crossfire").Any() || Process.GetProcessesByName("crossfire.exe").Any();
        game.Text = found ? "CrossFire: detected" : "CrossFire: not detected";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
