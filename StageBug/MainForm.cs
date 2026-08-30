using System.Drawing;
using System.Windows.Forms;

namespace StageBug;

public sealed class MainForm : Form
{
    private readonly Label status = new();
    private readonly Label game = new();
    private readonly Label session = new();
    private readonly Button initialize = new();
    private readonly Button boost1 = new();
    private readonly Button boost2 = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly SessionController controller = new();

    public MainForm()
    {
        Text = "StageBug";
        ClientSize = new Size(338, 340);
        MinimumSize = new Size(338, 340);
        MaximumSize = new Size(338, 340);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(20, 20, 20);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            Text = "StageBug",
            Dock = DockStyle.Top,
            Height = 42,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 16F, FontStyle.Bold)
        };

        game.Text = "CrossFire: not detected";
        game.AutoSize = false;
        game.Height = 24;
        game.Dock = DockStyle.Top;
        game.TextAlign = ContentAlignment.MiddleCenter;

        session.Text = "Session: idle";
        session.AutoSize = false;
        session.Height = 24;
        session.Dock = DockStyle.Top;
        session.TextAlign = ContentAlignment.MiddleCenter;

        status.Text = "Ready";
        status.AutoSize = false;
        status.Height = 30;
        status.Dock = DockStyle.Bottom;
        status.TextAlign = ContentAlignment.MiddleCenter;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24, 12, 24, 14)
        };
        for (int i = 0; i < 3; i++)
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 33.3333F));

        Configure(initialize, "INITIALIZE SESSION", InitializeSession);
        Configure(boost1, "TRIGGER BOOST 1", (_, _) => TriggerBoost(1));
        Configure(boost2, "TRIGGER BOOST 2", (_, _) => TriggerBoost(2));

        boost1.Enabled = false;
        boost2.Enabled = false;

        panel.Controls.Add(initialize, 0, 0);
        panel.Controls.Add(boost1, 0, 1);
        panel.Controls.Add(boost2, 0, 2);

        Controls.Add(panel);
        Controls.Add(status);
        Controls.Add(session);
        Controls.Add(game);
        Controls.Add(title);

        timer.Tick += (_, _) => UpdateGameState();
        timer.Start();
        UpdateGameState();
    }

    private static void Configure(Button button, string text, EventHandler handler)
    {
        button.Text = text;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 4, 0, 4);
        button.FlatStyle = FlatStyle.Flat;
        button.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        button.Click += handler;
    }

    private void InitializeSession(object? sender, EventArgs e)
    {
        if (controller.InitializeSession(out var message))
        {
            status.Text = message;
            UpdateGameState();
        }
        else
        {
            status.Text = message;
            UpdateGameState();
        }
    }

    private void TriggerBoost(int number)
    {
        var success = controller.TryTriggerBoost(number, out var message);
        status.Text = message;
        UpdateGameState();
    }

    private void UpdateGameState()
    {
        var found = controller.RefreshCrossFire();
        game.Text = found
            ? $"CrossFire: detected (PID {controller.CrossFireProcessId})"
            : "CrossFire: not detected";

        switch (controller.State)
        {
            case StageBugSessionState.Initialized:
                session.Text = "Session: initialized";
                break;
            case StageBugSessionState.Boost1Applied:
                session.Text = "Session: Boost 1 applied";
                break;
            case StageBugSessionState.Boost2Applied:
                session.Text = "Session: Boost 2 applied";
                break;
            case StageBugSessionState.CrossFireDetected:
                session.Text = "Session: CrossFire detected";
                break;
            default:
                session.Text = "Session: idle";
                break;
        }

        initialize.Enabled = found && controller.State is not StageBugSessionState.Boost1Applied and not StageBugSessionState.Boost2Applied;
        boost1.Enabled = controller.State == StageBugSessionState.Initialized;
        boost2.Enabled = controller.State == StageBugSessionState.Boost1Applied;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
