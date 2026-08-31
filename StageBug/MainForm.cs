using System.Drawing;
using System.Windows.Forms;

namespace StageBug;

public sealed class MainForm : Form
{
    private readonly Label status = new();
    private readonly Label game = new();
    private readonly Label session = new();
    private readonly Label room = new();
    private readonly Button initialize = new();
    private readonly Button boost1 = new();
    private readonly Button boost2 = new();
    private readonly Button restoreRoom = new();
    private readonly Button createRoom = new();
    private readonly Button leaveRoom = new();
    private readonly TextBox roomCode = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly SessionController controller = new();
    private readonly RoomStateModel roomController = new();

    public MainForm()
    {
        Text = "StageBug";
        ClientSize = new Size(380, 520);
        MinimumSize = new Size(380, 520);
        MaximumSize = new Size(380, 520);
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
        game.Height = 24;
        game.Dock = DockStyle.Top;
        game.TextAlign = ContentAlignment.MiddleCenter;

        session.Text = "Session: idle";
        session.Height = 24;
        session.Dock = DockStyle.Top;
        session.TextAlign = ContentAlignment.MiddleCenter;

        room.Text = FormatRoomState();
        room.Height = 24;
        room.Dock = DockStyle.Top;
        room.TextAlign = ContentAlignment.MiddleCenter;

        status.Text = "Ready";
        status.AutoSize = false;
        status.Height = 30;
        status.Dock = DockStyle.Bottom;
        status.TextAlign = ContentAlignment.MiddleCenter;

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(24, 10, 24, 12)
        };
        for (int i = 0; i < 6; i++)
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 16.6667F));

        Configure(initialize, "INITIALIZE SESSION", InitializeSession);
        Configure(boost1, "TRIGGER BOOST 1", (_, _) => TriggerBoost(1));
        Configure(boost2, "TRIGGER BOOST 2", (_, _) => TriggerBoost(2));
        Configure(createRoom, "CREATE ROOM", CreateRoom);
        Configure(leaveRoom, "LEAVE ROOM", LeaveRoom);

        var roomRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 4)
        };
        roomRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        roomRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        roomCode.Dock = DockStyle.Fill;
        roomCode.Margin = new Padding(0, 0, 6, 0);
        roomCode.PlaceholderText = "Room code";
        Configure(restoreRoom, "RESTORE ROOM", RestoreRoom);
        roomRow.Controls.Add(roomCode, 0, 0);
        roomRow.Controls.Add(restoreRoom, 1, 0);

        panel.Controls.Add(initialize, 0, 0);
        panel.Controls.Add(boost1, 0, 1);
        panel.Controls.Add(boost2, 0, 2);
        panel.Controls.Add(roomRow, 0, 3);
        panel.Controls.Add(createRoom, 0, 4);
        panel.Controls.Add(leaveRoom, 0, 5);

        Controls.Add(panel);
        Controls.Add(status);
        Controls.Add(room);
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
        var ok = controller.InitializeSession(out var message);
        status.Text = message;
        if (ok)
            roomController.Restore(out _);
        StageBugDiagnostics.Info($"Initialize session result: {ok}");
        UpdateControlState();
        UpdateRoomState();
    }

    private void TriggerBoost(int number)
    {
        var ok = controller.TryTriggerBoost(number, out var message);
        status.Text = message;
        StageBugDiagnostics.Info($"Boost {number} command result: {ok}");
        UpdateControlState();
    }

    private void CreateRoom(object? sender, EventArgs e)
    {
        if (controller.State is StageBugSessionState.Idle or StageBugSessionState.CrossFireDetected)
        {
            status.Text = "Initialize the session before creating a room.";
            return;
        }

        var ok = roomController.Create(out var message);
        status.Text = message;
        StageBugDiagnostics.Info($"Room create state result: {ok}");
        UpdateRoomState();
        UpdateControlState();
    }

    private void RestoreRoom(object? sender, EventArgs e)
    {
        var ok = roomController.Restore(out var message);
        status.Text = message;
        StageBugDiagnostics.Info($"Room restore result: {ok}");
        UpdateRoomState();
        UpdateControlState();
    }

    private void LeaveRoom(object? sender, EventArgs e)
    {
        var ok = roomController.Leave(out var message);
        status.Text = message;
        StageBugDiagnostics.Info($"Room leave result: {ok}");
        UpdateRoomState();
        UpdateControlState();
    }

    private void UpdateControlState()
    {
        var sessionReady = controller.State is StageBugSessionState.Initialized or StageBugSessionState.Boost1Applied;
        initialize.Enabled = controller.State != StageBugSessionState.Boost2Applied;
        boost1.Enabled = controller.State == StageBugSessionState.Initialized;
        boost2.Enabled = controller.State == StageBugSessionState.Boost1Applied;
        createRoom.Enabled = sessionReady && (roomController.State is RoomState.None or RoomState.Closed);
        restoreRoom.Enabled = roomController.State is RoomState.Restored or RoomState.Closed or RoomState.None;
        leaveRoom.Enabled = roomController.State is RoomState.Active or RoomState.Restored or RoomState.Creating;
    }

    private void UpdateGameState()
    {
        var found = controller.RefreshCrossFire();
        if (!found)
        {
            game.Text = "CrossFire: not detected";
            session.Text = "Session: idle";
        }
        else
        {
            var title = controller.Observation.MainWindowTitle;
            var readiness = controller.ClientWindowReady ? "window ready" : "window not ready";
            game.Text = $"CrossFire: detected (PID {controller.CrossFireProcessId}; {readiness})";
            session.Text = controller.State switch
            {
                StageBugSessionState.CrossFireDetected => "Session: CrossFire detected",
                StageBugSessionState.Initialized => "Session: initialized",
                StageBugSessionState.Boost1Applied => "Session: Boost 1 applied",
                StageBugSessionState.Boost2Applied => "Session: Boost 2 applied",
                _ => "Session: idle"
            };

            if (!string.IsNullOrWhiteSpace(title))
                StageBugDiagnostics.Info($"CrossFire window: {title}");
        }

        UpdateControlState();
        UpdateRoomState();
    }

    private void UpdateRoomState()
    {
        room.Text = FormatRoomState();
        if (roomController.State is RoomState.Restored or RoomState.Active)
            roomCode.Text = roomController.RoomCode ?? roomCode.Text;
    }

    private string FormatRoomState()
    {
        return roomController.State switch
        {
            RoomState.Active => $"Room: active ({roomController.RoomCode})",
            RoomState.Restored => $"Room: restored ({roomController.RoomCode})",
            RoomState.Creating => "Room: creating",
            RoomState.Closed => "Room: closed",
            _ => "Room: none"
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
