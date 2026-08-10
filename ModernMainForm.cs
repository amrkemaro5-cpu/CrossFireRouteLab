using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed class ModernMainForm : Form
{
    static readonly Color Bg = Color.FromArgb(2, 5, 13);
    static readonly Color Surface = Color.FromArgb(6, 12, 25);
    static readonly Color Surface2 = Color.FromArgb(9, 18, 35);
    static readonly Color Line = Color.FromArgb(25, 49, 80);
    static readonly Color Cyan = Color.FromArgb(0, 224, 255);
    static readonly Color Purple = Color.FromArgb(181, 70, 255);
    static readonly Color Blue = Color.FromArgb(67, 126, 255);
    static readonly Color Green = Color.FromArgb(34, 240, 106);
    static readonly Color Yellow = Color.FromArgb(255, 207, 56);
    static readonly Color Red = Color.FromArgb(255, 74, 115);
    static readonly Color TextColor = Color.FromArgb(235, 244, 255);
    static readonly Color Muted = Color.FromArgb(132, 158, 190);

    readonly FlowLayoutPanel games = new();
    readonly RichTextBox console = new();
    readonly Label gameName = new(), gameMeta = new(), network = new(), router = new(), best = new(), metrics = new(), quality = new(), tips = new(), progressText = new(), systemText = new(), connections = new(), analysisTitle = new(), currentIconPath = new();
    readonly TextBox endpoint = new();
    readonly GlowProgress progress = new();
    readonly RadarControl radar = new();
    readonly SparklineControl graph = new();
    readonly List<NeonActionButton> actions = new();
    readonly List<GameProfile> memory = new();
    readonly System.Windows.Forms.Timer animationTimer = new() { Interval = 32 };
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab");
    GameProfile? current;
    bool busy;
    float animationPhase;

    public ModernMainForm()
    {
        Text = "Game Route Lab";
        ClientSize = new Size(1536, 900);
        MinimumSize = new Size(1180, 780);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Directory.CreateDirectory(dataDir);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        memory.AddRange(GameProfileStore.Load());
        BuildUi();
        RefreshMemory();
        Log("GAME ROUTE LAB v7.0");
        Log("Smart game detection • ISP • router • endpoint • route quality • local game memory");
        Log("READ-ONLY MODE: no Windows routes, DNS, PPPoE, router settings or firmware are changed.");

        animationTimer.Tick += (_, _) =>
        {
            animationPhase += 0.045f;
            radar.Phase = animationPhase;
            progress.Phase = animationPhase;
            graph.Phase = animationPhase;
            foreach (var a in actions) a.Phase = animationPhase;
            radar.Invalidate();
            progress.Invalidate();
            graph.Invalidate();
        };
        animationTimer.Start();
        FormClosed += (_, _) => animationTimer.Stop();
    }

    void BuildUi()
    {
        Controls.Clear();
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Bg,
            ColumnCount = 3,
            RowCount = 1,
            Padding = new Padding(14, 10, 14, 8),
            Margin = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        body.Controls.Add(BuildLeft(), 0, 0);
        body.Controls.Add(BuildCenter(), 1, 0);
        body.Controls.Add(BuildRight(), 2, 0);
        root.Controls.Add(body, 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);
    }

    Control BuildHeader()
    {
        var p = new NeonHeader { Dock = DockStyle.Fill };
        p.Controls.Add(new PictureBox
        {
            Image = Brand.CreateLogo(104),
            Size = new Size(112, 112),
            Location = new Point(28, 8),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent
        });
        p.Controls.Add(LabelOf("GAME ROUTE LAB", new Point(150, 25), new Size(700, 38), 30, TextColor, true));
        p.Controls.Add(LabelOf("SMARTER ROUTES.  BETTER PING.", new Point(154, 67), new Size(700, 22), 12, Cyan, true));
        p.Controls.Add(LabelOf("LOCAL-FIRST GAME NETWORK ANALYZER", new Point(155, 94), new Size(700, 20), 9, Muted));

        var badge = new StatusBadge { Size = new Size(250, 70), Accent = Green, Title = "SYSTEM STATUS", Value = "READY • READ-ONLY" };
        p.Controls.Add(badge);
        p.Resize += (_, _) => badge.Location = new Point(Math.Max(640, p.ClientSize.Width - badge.Width - 28), 25);
        return p;
    }

    Control BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(3, 7, 16), Padding = new Padding(12, 7, 12, 6) };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 1, 0, 0),
            Margin = Padding.Empty
        };
        flow.Controls.Add(LabelOf("ENDPOINT", Point.Empty, new Size(62, 76), 8, Muted, true, ContentAlignment.MiddleLeft));
        endpoint.Width = 172;
        endpoint.Height = 36;
        endpoint.Margin = new Padding(0, 18, 8, 0);
        endpoint.BackColor = Surface2;
        endpoint.ForeColor = TextColor;
        endpoint.BorderStyle = BorderStyle.FixedSingle;
        endpoint.PlaceholderText = "Optional IP / hostname";
        flow.Controls.Add(endpoint);

        AddAction(flow, ActionIcon.Radar, "AUTO ANALYZE", AutoAnalyze, Purple);
        AddAction(flow, ActionIcon.Gamepad, "REFRESH GAMES", RefreshGames, Cyan);
        AddAction(flow, ActionIcon.Network, "DETECT NETWORK", DetectNetwork, Cyan);
        AddAction(flow, ActionIcon.Router, "DETECT ROUTER", DetectRouter, Purple);
        AddAction(flow, ActionIcon.Search, "FIND CONNECTIONS", FindConnections, Cyan);
        AddAction(flow, ActionIcon.Route, "ROUTE TABLE", RouteTable, Blue);
        AddAction(flow, ActionIcon.Ping, "PING 30x", Ping30, Green);
        AddAction(flow, ActionIcon.Trace, "TRACEROUTE", Traceroute, Purple);
        AddAction(flow, ActionIcon.Chart, "PATH QUALITY", PathQuality, Green);
        AddAction(flow, ActionIcon.Report, "SAVE REPORT", SaveReport, Purple);
        bar.Controls.Add(flow);
        return bar;
    }

    void AddAction(Control parent, ActionIcon icon, string title, Func<Task> action, Color accent)
    {
        var b = new NeonActionButton(icon, title, accent)
        {
            Width = 106,
            Height = 76,
            Margin = new Padding(3, 0, 3, 0)
        };
        b.Click += async (_, _) => await Safe(action);
        actions.Add(b);
        parent.Controls.Add(b);
    }

    Control BuildLeft()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 160));

        var memoryCard = new NeonCard { Dock = DockStyle.Fill, Accent = Purple, Padding = new Padding(12) };
        memoryCard.Controls.Add(LabelOf("GAME MEMORY", new Point(14, 12), new Size(220, 25), 13, Purple, true));
        memoryCard.Controls.Add(LabelOf("YOUR LOCAL HISTORY", new Point(14, 37), new Size(220, 18), 8, Muted, true));
        games.FlowDirection = FlowDirection.TopDown;
        games.WrapContents = false;
        games.AutoScroll = true;
        games.Dock = DockStyle.Fill;
        games.Padding = new Padding(0, 62, 0, 52);
        games.BackColor = Color.Transparent;
        memoryCard.Controls.Add(games);

        var all = new NeonButton("VIEW ALL GAMES", Purple) { Height = 38, Dock = DockStyle.Bottom, Margin = new Padding(0) };
        all.Click += (_, _) => AllGames();
        memoryCard.Controls.Add(all);
        root.Controls.Add(memoryCard, 0, 0);

        var quick = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan, Padding = new Padding(12) };
        quick.Controls.Add(LabelOf("QUICK ACTIONS", new Point(14, 10), new Size(220, 22), 12, Cyan, true));
        AddQuick(quick, "CLEAR MEMORY", Purple, 39, () => { memory.Clear(); ClearStoredMemory(); RefreshMemory(); });
        AddQuick(quick, "EXPORT ALL REPORTS", Cyan, 76, () => SaveReport().GetAwaiter().GetResult());
        AddQuick(quick, "SETTINGS", Purple, 113, () => MessageBox.Show("Game Route Lab runs in READ-ONLY mode. It does not change router, DNS, PPPoE or Windows route settings.", "Game Route Lab", MessageBoxButtons.OK, MessageBoxIcon.Information));
        root.Controls.Add(quick, 0, 1);
        return root;
    }

    void AddQuick(Control parent, string text, Color accent, int y, Action action)
    {
        var b = new NeonButton(text, accent) { Location = new Point(14, y), Size = new Size(220, 32) };
        b.Click += (_, _) => action();
        parent.Controls.Add(b);
    }

    Control BuildCenter()
    {
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 18));
        center.Controls.Add(BuildHero(), 0, 0);
        center.Controls.Add(BuildSummary(), 0, 1);
        center.Controls.Add(BuildBest(), 0, 2);
        center.Controls.Add(BuildConsole(), 0, 3);
        return center;
    }

    Control BuildHero()
    {
        var card = new NeonCard { Dock = DockStyle.Fill, Accent = Purple, Padding = new Padding(12) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(radar, 0, 0);
        radar.Dock = DockStyle.Fill;
        radar.Margin = new Padding(4, 4, 8, 4);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        analysisTitle.Text = "AUTO ANALYSIS READY";
        analysisTitle.Dock = DockStyle.Fill;
        analysisTitle.Font = new Font("Segoe UI Semibold", 19, FontStyle.Bold);
        analysisTitle.ForeColor = Purple;
        right.Controls.Add(analysisTitle, 0, 0);
        right.Controls.Add(LabelOf("Detecting the game, connections and route quality automatically...", Point.Empty, Size.Empty, 9.5f, Muted), 0, 1);

        var progressRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        progress.Dock = DockStyle.Fill;
        progress.Margin = new Padding(0, 3, 10, 3);
        progressRow.Controls.Add(progress, 0, 0);
        progressText.Text = "READY";
        progressText.Dock = DockStyle.Fill;
        progressText.TextAlign = ContentAlignment.MiddleRight;
        progressText.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
        progressText.ForeColor = TextColor;
        progressRow.Controls.Add(progressText, 1, 0);
        right.Controls.Add(progressRow, 0, 2);

        var stages = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0), AutoScroll = true };
        stages.Controls.Add(new StageItem("1", "DETECT GAME", Green));
        stages.Controls.Add(new StageItem("2", "FIND CONNECTIONS", Green));
        stages.Controls.Add(new StageItem("3", "TEST ENDPOINTS", Green));
        stages.Controls.Add(new StageItem("4", "ANALYZE ROUTES", Purple));
        stages.Controls.Add(new StageItem("5", "GENERATE REPORT", Muted));
        right.Controls.Add(stages, 0, 3);
        layout.Controls.Add(right, 1, 0);
        card.Controls.Add(layout);
        return card;
    }

    Control BuildSummary()
    {
        var card = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan, Padding = new Padding(12) };
        card.Controls.Add(LabelOf("CURRENT ANALYSIS SUMMARY", new Point(14, 10), new Size(500, 25), 13, Cyan, true));

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(0, 36, 0, 0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var active = new NeonInnerCard { Dock = DockStyle.Fill, Accent = Cyan, Margin = new Padding(2, 0, 6, 2) };
        active.Controls.Add(LabelOf("ACTIVE GAME", new Point(16, 12), new Size(250, 22), 11, Cyan, true));
        gameName.Text = "No game detected";
        gameName.Location = new Point(16, 43);
        gameName.Size = new Size(360, 28);
        gameName.Font = new Font("Segoe UI Semibold", 14.5f, FontStyle.Bold);
        active.Controls.Add(gameName);
        gameMeta.Text = "Start an online game and click AUTO ANALYZE";
        gameMeta.Location = new Point(16, 76);
        gameMeta.Size = new Size(390, 70);
        gameMeta.ForeColor = Muted;
        active.Controls.Add(gameMeta);
        layout.Controls.Add(active, 0, 0);

        var conn = new NeonInnerCard { Dock = DockStyle.Fill, Accent = Cyan, Margin = new Padding(6, 0, 2, 2) };
        conn.Controls.Add(LabelOf("CONNECTIONS DISCOVERED", new Point(16, 12), new Size(360, 22), 11, Cyan, true));
        connections.Text = "No endpoints discovered yet.\r\n\r\nAUTO ANALYZE will find public connections for the active game automatically.";
        connections.Location = new Point(16, 44);
        connections.Size = new Size(430, 110);
        connections.ForeColor = Muted;
        connections.Font = new Font("Cascadia Mono", 8.8f);
        conn.Controls.Add(connections);
        layout.Controls.Add(conn, 1, 0);

        card.Controls.Add(layout);
        return card;
    }

    Control BuildBest()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var bestCard = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan, Padding = new Padding(14), Margin = new Padding(0, 2, 5, 0) };
        bestCard.Controls.Add(LabelOf("BEST ENDPOINT (CURRENT)", new Point(14, 10), new Size(360, 24), 12, Cyan, true));
        best.Text = "—";
        best.Location = new Point(14, 42);
        best.Size = new Size(520, 29);
        best.Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold);
        bestCard.Controls.Add(best);
        metrics.Text = "LATENCY     — ms\r\nLOSS        —\r\nJITTER      — ms\r\nSTABILITY   —";
        metrics.Location = new Point(14, 76);
        metrics.Size = new Size(330, 82);
        metrics.Font = new Font("Segoe UI Semibold", 9.8f, FontStyle.Bold);
        metrics.ForeColor = Green;
        bestCard.Controls.Add(metrics);
        row.Controls.Add(bestCard, 0, 0);

        var qualityCard = new NeonCard { Dock = DockStyle.Fill, Accent = Green, Padding = new Padding(14), Margin = new Padding(5, 2, 0, 0) };
        qualityCard.Controls.Add(LabelOf("ROUTE QUALITY", new Point(14, 10), new Size(220, 24), 12, Cyan, true));
        quality.Text = "WAITING";
        quality.Dock = DockStyle.Top;
        quality.Height = 24;
        quality.TextAlign = ContentAlignment.MiddleRight;
        quality.Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
        quality.ForeColor = Muted;
        qualityCard.Controls.Add(quality);
        graph.Dock = DockStyle.Fill;
        graph.Margin = new Padding(0, 38, 0, 25);
        qualityCard.Controls.Add(graph);
        qualityCard.Controls.Add(LabelOf("Hops: —   |   Avg Latency: —", new Point(14, 0), new Size(360, 20), 8.5f, Muted));
        row.Controls.Add(qualityCard, 1, 0);
        return row;
    }

    Control BuildConsole()
    {
        var card = new NeonCard { Dock = DockStyle.Fill, Accent = Blue, Padding = new Padding(10) };
        card.Controls.Add(LabelOf("LIVE ANALYSIS CONSOLE", new Point(14, 7), new Size(360, 22), 10.5f, Cyan, true));
        console.Location = new Point(10, 34);
        console.Size = new Size(700, 100);
        console.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        console.BackColor = Color.FromArgb(1, 4, 10);
        console.ForeColor = TextColor;
        console.BorderStyle = BorderStyle.None;
        console.Font = new Font("Cascadia Mono", 8.2f);
        console.ReadOnly = true;
        console.WordWrap = false;
        card.Controls.Add(console);
        return card;
    }

    Control BuildRight()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 31));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));

        var n = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 5) };
        n.Controls.Add(LabelOf("NETWORK INFORMATION", new Point(14, 12), new Size(280, 24), 11.5f, Cyan, true));
        network.Text = "ISP\t—\r\nASN\t—\r\nPublic IP\t—\r\nLocation\t—\r\nConnection\t—\r\nDNS\t—";
        network.Location = new Point(16, 47);
        network.Size = new Size(280, 150);
        network.Font = new Font("Segoe UI", 9.2f);
        n.Controls.Add(network);
        root.Controls.Add(n, 0, 0);

        var r = new NeonCard { Dock = DockStyle.Fill, Accent = Purple, Padding = new Padding(12), Margin = new Padding(0, 2, 0, 5) };
        r.Controls.Add(LabelOf("ROUTER INTELLIGENCE", new Point(14, 12), new Size(280, 24), 11.5f, Purple, true));
        router.Text = "Gateway\t—\r\nManufacturer\t—\r\nModel\t—\r\nFirmware\t—\r\nInterface\t—\r\nConfidence\t—";
        router.Location = new Point(16, 47);
        router.Size = new Size(285, 190);
        router.Font = new Font("Segoe UI", 9.2f);
        r.Controls.Add(router);
        root.Controls.Add(r, 0, 1);

        var z = new NeonCard { Dock = DockStyle.Fill, Accent = Purple, Padding = new Padding(12) };
        z.Controls.Add(LabelOf("TIPS", new Point(14, 12), new Size(280, 24), 11.5f, Purple, true));
        tips.Text = "Run analysis while the game is in an online match.\r\n\r\nMore observations = better local memory.\r\n\r\nICMP-blocked servers are not automatically treated as packet loss.\r\n\r\nThe analyzer never changes your router or Windows routes.";
        tips.Location = new Point(16, 47);
        tips.Size = new Size(285, 250);
        tips.ForeColor = Muted;
        tips.Font = new Font("Segoe UI", 9.1f);
        z.Controls.Add(tips);
        root.Controls.Add(z, 0, 2);
        return root;
    }

    Control BuildFooter()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(2, 6, 15) };
        p.Controls.Add(LabelOf("Game Route Lab v7.0  •  READ-ONLY MODE", new Point(18, 8), new Size(300, 22), 9.2f, Green, true));
        systemText.Text = "SYSTEM: Windows 64-bit";
        systemText.AutoSize = true;
        systemText.Location = new Point(330, 8);
        systemText.ForeColor = Cyan;
        p.Controls.Add(systemText);
        var ready = LabelOf("●  READY", Point.Empty, new Size(90, 22), 9.2f, Green, true, ContentAlignment.MiddleRight);
        p.Controls.Add(ready);
        p.Resize += (_, _) => ready.Location = new Point(p.ClientSize.Width - ready.Width - 18, 8);
        return p;
    }

    Label LabelOf(string text, Point location, Size size, float font, Color color, bool bold = false, ContentAlignment align = ContentAlignment.TopLeft)
        => new() { Text = text, Location = location, Size = size, Font = new Font("Segoe UI", font, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent, TextAlign = align };

    async Task Safe(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        foreach (var a in actions) a.Enabled = false;
        try
        {
            SetProgress(5, "5%");
            await action();
        }
        catch (Exception ex)
        {
            Log("[ERROR] " + ex.Message);
            quality.Text = "ERROR";
            quality.ForeColor = Red;
        }
        finally
        {
            busy = false;
            foreach (var a in actions) a.Enabled = true;
            if (progress.Value < 100) SetProgress(100, "READY");
        }
    }

    void SetProgress(int value, string text)
    {
        progress.Value = Math.Clamp(value, 0, 100);
        progressText.Text = text;
        radar.Invalidate();
    }

    async Task AutoAnalyze()
    {
        analysisTitle.Text = "AUTO ANALYSIS IN PROGRESS";
        quality.Text = "ANALYZING";
        quality.ForeColor = Purple;
        SetProgress(8, "8%");
        Log("\r\n============================================================\r\nAUTO ANALYSIS STARTED\r\n============================================================");

        await DetectNetwork();
        SetProgress(22, "22%");
        await DetectRouter();
        SetProgress(38, "38%");

        var gamesFound = await DiscoverGames();
        if (gamesFound.Count == 0)
        {
            gameName.Text = "No game detected";
            gameMeta.Text = "Start an online game and run AUTO ANALYZE again.";
            connections.Text = "No public game endpoints found yet.\r\n\r\nThe scanner ignores ChatGPT, browsers, launchers and normal Windows services.";
            quality.Text = "WAITING";
            quality.ForeColor = Muted;
            analysisTitle.Text = "AUTO ANALYSIS READY";
            Log("STOP: no high-confidence game was detected. No non-game application was added to memory.");
            return;
        }

        current = gamesFound[0];
        gameName.Text = current.DisplayName;
        gameMeta.Text = $"{current.Observations} saved analyses\r\nPath: {current.ExecutablePath}\r\nLast best: {(string.IsNullOrWhiteSpace(current.LastBestEndpoint) ? "—" : current.LastBestEndpoint)}";
        SetProgress(48, "48%");
        Log($"GAME: {current.DisplayName} | executable identified | saved memory loaded");

        var live = (await GameScanner.DiscoverAsync()).FirstOrDefault(x =>
            x.ExecutablePath.Equals(current.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
            x.ProcessName.Equals(current.ProcessName, StringComparison.OrdinalIgnoreCase));
        var endpoints = live == null ? new List<GameEndpoint>() : await GetEndpoints(live.Pid);
        connections.Text = endpoints.Count == 0
            ? "Game detected, but no public established sockets are visible.\r\n\r\nEnter an online match and retry."
            : string.Join("\r\n", endpoints.Take(5).Select(x => $"{x.Protocol}  {x.RemoteIp}:{x.RemotePort}   {x.State}")) + (endpoints.Count > 5 ? $"\r\n… and {endpoints.Count - 5} more" : "");
        Log($"FOUND {endpoints.Count} candidate game endpoint(s). Testing automatically — no IP copying required.");
        if (endpoints.Count == 0) return;

        SetProgress(62, "62%");
        var results = new List<RouteResult>();
        foreach (var ep in endpoints.Take(8))
        {
            Log($"TESTING {ep.RemoteIp}:{ep.RemotePort}/{ep.Protocol} ...");
            results.Add(new RouteResult(ep, await Probe(ep.RemoteIp), await Trace(ep.RemoteIp)));
        }

        var bestRoute = results.OrderBy(x => x.Score).First();
        ApplyResult(bestRoute);
        GameProfileStore.Record(current, $"{bestRoute.Endpoint.RemoteIp}:{bestRoute.Endpoint.RemotePort}", Math.Max(0, 100 - bestRoute.Score), $"hops={bestRoute.Trace.Hops}; last={bestRoute.Trace.Last:0}ms");
        memory.Clear();
        memory.AddRange(GameProfileStore.Load());
        current = memory.FirstOrDefault(x => x.Key == current.Key) ?? current;
        RefreshMemory();
        SetProgress(100, "100%");
        analysisTitle.Text = "ANALYSIS COMPLETE";
        Log($"BEST: {bestRoute.Endpoint.RemoteIp}:{bestRoute.Endpoint.RemotePort} | {bestRoute.Probe.Avg:0} ms | loss {bestRoute.Probe.Loss:0}% | hops {bestRoute.Trace.Hops}");
        Log("ICMP timeout is treated as blocked/unknown evidence, not automatic game packet loss.");
    }

    void ApplyResult(RouteResult result)
    {
        best.Text = $"{result.Endpoint.RemoteIp}:{result.Endpoint.RemotePort}   ({result.Endpoint.Protocol})";
        metrics.Text = $"LATENCY     {(result.Probe.Avg > 0 ? result.Probe.Avg.ToString("0") : "—")} ms\r\nLOSS        {(result.Probe.HasResponse ? result.Probe.Loss.ToString("0") : "unknown")}\r\nJITTER      {(result.Probe.HasResponse ? result.Probe.Jitter.ToString("0") : "—")} ms\r\nSTABILITY   {result.Stability}";
        quality.Text = result.Stability.ToUpperInvariant();
        quality.ForeColor = result.Stability == "Excellent" ? Green : result.Stability == "Good" ? Yellow : result.Stability == "Unknown" ? Muted : Red;
        graph.Values = result.Probe.History.Count > 0 ? result.Probe.History : new List<double> { 1 };
        graph.Invalidate();
    }

    async Task RefreshGames()
    {
        var found = await DiscoverGames();
        Log($"\r\n=== GAME DISCOVERY: {found.Count} candidate(s) ===");
        foreach (var g in found) Log($"{g.DisplayName} | {g.ProcessName} | {g.Observations} saved observations | {g.ExecutablePath}");
    }

    async Task<List<GameProfile>> DiscoverGames()
    {
        var items = await GameScanner.DiscoverAsync();
        var candidates = items
            .Where(x => x.LikelyGame && !GameProfileStore.IsBlocked(x.ProcessName))
            .GroupBy(x => new { x.Pid, x.ProcessName, x.ExecutablePath })
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(x => x.Confidence)
            .Take(12)
            .ToList();

        foreach (var g in candidates)
        {
            try { GameProfileStore.Touch(g.ProcessName, g.ExecutablePath); }
            catch { }
        }
        memory.Clear();
        memory.AddRange(GameProfileStore.Load());
        RefreshMemory();
        return candidates.Select(g => memory.FirstOrDefault(p => p.ProcessName.Equals(g.ProcessName, StringComparison.OrdinalIgnoreCase) && p.ExecutablePath.Equals(g.ExecutablePath, StringComparison.OrdinalIgnoreCase))).Where(p => p != null).Cast<GameProfile>().ToList();
    }

    async Task DetectNetwork()
    {
        var n = await NetworkProfileDetector.DetectAsync();
        network.Text = $"ISP\t{n.ISP}\r\nASN\t{n.ASN}\r\nPublic IP\t{n.PublicIp}\r\nLocation\t{n.City}, {n.Country}\r\nConnection\t{n.WanType}\r\nDNS\t{n.DnsServers}";
        systemText.Text = $"SYSTEM: Windows {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}  •  {n.InterfaceName}";
        Log($"NETWORK: ISP={n.ISP} | Org={n.Organization} | ASN={n.ASN} | Public={n.PublicIp} | GW={n.Gateway}");
    }

    async Task DetectRouter()
    {
        var r = await RouterDetector.DetectAsync();
        router.Text = $"Gateway\t{r.Gateway}\r\nManufacturer\t{r.Vendor}\r\nModel\t{r.Model}\r\nFirmware\t{r.Firmware}\r\nInterface\t{r.ManagementUrl}\r\nConfidence\t{r.Confidence}";
        Log($"ROUTER: {r.Vendor} {r.Model} | firmware {r.Firmware} | confidence {r.Confidence}");
    }

    async Task FindConnections()
    {
        var found = await GameScanner.DiscoverAsync();
        var game = found.FirstOrDefault(x => x.LikelyGame);
        if (game == null) { Log("No high-confidence game connection found. ChatGPT and browser processes are excluded."); return; }
        var eps = await GetEndpoints(game.Pid);
        connections.Text = eps.Count == 0 ? "No public established sockets visible." : string.Join("\r\n", eps.Select(x => $"{x.Protocol}  {x.RemoteIp}:{x.RemotePort}   {x.State}"));
        foreach (var ep in eps) Log($"{ep.Protocol} {ep.RemoteIp}:{ep.RemotePort} {ep.State}");
    }

    async Task RouteTable() => Log("\r\n=== ROUTE TABLE ===\r\n" + await Run("route.exe", "print", 10000));

    async Task Ping30()
    {
        var ip = Target();
        if (ip.Length == 0) { Log("No endpoint selected."); return; }
        Log($"\r\n=== PING 30x {ip} ===\r\n" + await Run("ping.exe", $"-n 30 {ip}", 50000));
    }

    async Task Traceroute()
    {
        var ip = Target();
        if (ip.Length == 0) { Log("No endpoint selected."); return; }
        Log($"\r\n=== TRACEROUTE {ip} ===\r\n" + await Run("tracert.exe", $"-d -h 30 -w 700 {ip}", 45000));
    }

    async Task PathQuality()
    {
        var ip = Target();
        if (ip.Length == 0) { Log("No endpoint selected."); return; }
        var p = await Probe(ip);
        var t = await Trace(ip);
        var ep = new GameEndpoint("manual", 0, "TCP", ip, 0, "MANUAL", false, 0, "");
        ApplyResult(new RouteResult(ep, p, t));
        Log($"PATH QUALITY: {(p.HasResponse ? p.Avg.ToString("0") : "blocked/unknown")} ms | loss {(p.HasResponse ? p.Loss.ToString("0") : "unknown")} | jitter {(p.HasResponse ? p.Jitter.ToString("0") : "unknown")} | hops {t.Hops}");
    }

    async Task SaveReport()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"GameRouteLab_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var text = console.Text + Environment.NewLine + "=== CURRENT RESULT ===" + Environment.NewLine + best.Text + Environment.NewLine + metrics.Text;
        await File.WriteAllTextAsync(file, text);
        Log("Report saved: " + file);
    }

    string Target()
    {
        if (!string.IsNullOrWhiteSpace(endpoint.Text)) return endpoint.Text.Trim();
        if (string.IsNullOrWhiteSpace(current?.LastBestEndpoint)) return "";
        var s = current.LastBestEndpoint;
        var k = s.LastIndexOf(':');
        return k > 0 ? s[..k] : s;
    }

    async Task<List<GameEndpoint>> GetEndpoints(int pid)
    {
        var all = await GameScanner.DiscoverAsync();
        return all.Where(x => x.Pid == pid && x.LikelyGame).GroupBy(x => $"{x.Protocol}|{x.RemoteIp}|{x.RemotePort}").Select(g => g.First()).ToList();
    }

    async Task<ProbeResult> Probe(string host)
    {
        var samples = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                using var ping = new Ping();
                var r = await ping.SendPingAsync(host, 900);
                if (r.Status == IPStatus.Success) samples.Add(r.RoundtripTime);
            }
            catch { }
        }
        if (samples.Count == 0) return new ProbeResult(0, 0, 0, new List<double>(), false);
        var avg = samples.Average();
        var jitter = samples.Count < 2 ? 0 : samples.Zip(samples.Skip(1), (a, b) => Math.Abs(a - b)).Average();
        return new ProbeResult(avg, (5 - samples.Count) * 20, jitter, samples.Select(x => (double)x).ToList(), true);
    }

    async Task<TraceResult> Trace(string host)
    {
        var text = await Run("tracert.exe", $"-d -h 18 -w 500 {host}", 24000);
        var hops = 0;
        var last = 0.0;
        foreach (var line in text.Split('\n'))
        {
            if (!Regex.IsMatch(line.TrimStart(), @"^\d+\s+")) continue;
            hops++;
            var ms = Regex.Matches(line, @"(\d+)\s*ms");
            if (ms.Count > 0 && double.TryParse(ms[^1].Groups[1].Value, out var value)) last = value;
        }
        return new TraceResult(hops, last);
    }

    async Task<string> Run(string file, string args, int timeout)
    {
        try
        {
            using var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            p.Start();
            var outputTask = p.StandardOutput.ReadToEndAsync();
            var errorTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(true); } catch { }
                return await outputTask + Environment.NewLine + await errorTask + Environment.NewLine + "[Timed out]";
            }
            return await outputTask + Environment.NewLine + await errorTask;
        }
        catch (Exception ex) { return "[Command error] " + ex.Message; }
    }

    void RefreshMemory()
    {
        games.SuspendLayout();
        games.Controls.Clear();
        var clean = memory.Where(g => !GameProfileStore.IsBlocked(g.ProcessName)).OrderByDescending(x => x.LastSeenUtc).Take(8).ToList();
        foreach (var g in clean)
        {
            var item = new GameMemoryItem(g, Green, Cyan) { Width = Math.Max(205, games.ClientSize.Width - 8), Height = 78, Margin = new Padding(0, 2, 0, 5) };
            item.Click += (_, _) => SelectGame(g);
            games.Controls.Add(item);
        }
        if (clean.Count == 0)
        {
            games.Controls.Add(LabelOf("No games remembered yet.\r\n\r\nStart a game and click\r\nAUTO ANALYZE.", Point.Empty, new Size(220, 100), 9.5f, Muted));
        }
        games.ResumeLayout();
    }

    void SelectGame(GameProfile profile)
    {
        if (GameProfileStore.IsBlocked(profile.ProcessName)) return;
        current = profile;
        gameName.Text = profile.DisplayName;
        gameMeta.Text = $"{profile.Observations} saved analyses\r\nPath: {profile.ExecutablePath}\r\nBest: {(string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint)}";
        best.Text = string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint;
        Log($"\r\n[MEMORY] {profile.DisplayName}\r\nObservations: {profile.Observations}\r\nLast best: {profile.LastBestEndpoint}");
    }

    void AllGames()
    {
        using var f = new Form { Text = "Game Route Lab • All Games", Size = new Size(760, 560), BackColor = Bg, StartPosition = FormStartPosition.CenterParent, ForeColor = TextColor, MinimumSize = new Size(600, 420) };
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = TextColor, Font = new Font("Segoe UI", 10.5f), BorderStyle = BorderStyle.None };
        foreach (var g in memory.Where(x => !GameProfileStore.IsBlocked(x.ProcessName)).OrderByDescending(x => x.LastSeenUtc))
            list.Items.Add($"{g.DisplayName}    •    {g.Observations} analyses    •    Best {g.LastBestEndpoint}");
        f.Controls.Add(list);
        f.ShowDialog(this);
    }

    void ClearStoredMemory()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab");
            var file = Path.Combine(root, "profiles.json");
            if (File.Exists(file)) File.Delete(file);
        }
        catch { }
    }

    void Log(string text)
    {
        if (console.InvokeRequired) { console.BeginInvoke(() => Log(text)); return; }
        console.AppendText(text + Environment.NewLine);
        console.SelectionStart = console.TextLength;
        console.ScrollToCaret();
    }

    record ProbeResult(double Avg, double Loss, double Jitter, List<double> History, bool HasResponse);
    record TraceResult(int Hops, double Last);
    record RouteResult(GameEndpoint Endpoint, ProbeResult Probe, TraceResult Trace)
    {
        public double Score => Probe.HasResponse ? Math.Min(200, Probe.Avg + Probe.Loss * 2 + Trace.Hops * 0.5 + Probe.Jitter * 0.35) : 80 + Trace.Hops * 0.5;
        public string Stability => !Probe.HasResponse ? "Unknown" : Probe.Loss == 0 && Probe.Avg < 80 && Probe.Jitter < 12 ? "Excellent" : Probe.Loss < 20 && Probe.Avg < 150 ? "Good" : "Variable";
    }
}

enum ActionIcon { Radar, Gamepad, Network, Router, Search, Route, Ping, Trace, Chart, Report }

sealed class NeonHeader : Panel
{
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new LinearGradientBrush(ClientRectangle, Color.FromArgb(1, 3, 9), Color.FromArgb(4, 8, 20), LinearGradientMode.Horizontal);
        e.Graphics.FillRectangle(b, ClientRectangle);
        using var line = new LinearGradientBrush(new Rectangle(0, Height - 4, Width, 4), ModernMainFormColor.Purple, ModernMainFormColor.Cyan, LinearGradientMode.Horizontal);
        e.Graphics.FillRectangle(line, 0, Height - 4, Width, 4);
    }
}

sealed class NeonCard : Panel
{
    public Color Accent { get; set; } = Color.FromArgb(0, 224, 255);
    public NeonCard() { DoubleBuffered = true; BackColor = Color.FromArgb(5, 10, 21); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 4 || Height < 4) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(1, 1, Width - 3, Height - 3);
        using var fill = new LinearGradientBrush(ClientRectangle, Color.FromArgb(7, 14, 29), Color.FromArgb(3, 8, 18), LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(fill, ClientRectangle);
        using var p = new Pen(Color.FromArgb(90, Accent.R, Accent.G, Accent.B), 1);
        e.Graphics.DrawRectangle(p, r);
        using var glow = new Pen(Color.FromArgb(32, Accent.R, Accent.G, Accent.B), 2);
        e.Graphics.DrawLine(glow, 12, 2, Math.Min(220, Width - 12), 2);
    }
}

sealed class NeonInnerCard : Panel
{
    public Color Accent { get; set; } = Color.FromArgb(0, 224, 255);
    public NeonInnerCard() { DoubleBuffered = true; BackColor = Color.FromArgb(7, 16, 31); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Width < 2 || Height < 2) return;
        using var fill = new SolidBrush(Color.FromArgb(7, 16, 31));
        e.Graphics.FillRectangle(fill, ClientRectangle);
        using var p = new Pen(Color.FromArgb(70, Accent.R, Accent.G, Accent.B));
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }
}

sealed class NeonButton : Button
{
    readonly Color accent;
    public NeonButton(string text, Color accent)
    {
        Text = text;
        this.accent = accent;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 1;
        FlatAppearance.BorderColor = Color.FromArgb(110, accent.R, accent.G, accent.B);
        FlatAppearance.MouseOverBackColor = Color.FromArgb(22, 18, 42);
        BackColor = Color.FromArgb(6, 13, 27);
        ForeColor = Color.White;
        Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
    }
}

sealed class NeonActionButton : Panel
{
    readonly ActionIcon icon;
    readonly string title;
    readonly Color accent;
    bool hover;
    public float Phase { get; set; }

    public NeonActionButton(ActionIcon icon, string title, Color accent)
    {
        this.icon = icon;
        this.title = title;
        this.accent = accent;
        BackColor = Color.FromArgb(4, 10, 21);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        MouseEnter += (_, _) => { hover = true; Invalidate(); };
        MouseLeave += (_, _) => { hover = false; Invalidate(); };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bg = hover ? Color.FromArgb(14, 18, 38) : Color.FromArgb(4, 10, 21);
        using var fill = new SolidBrush(bg);
        e.Graphics.FillRectangle(fill, ClientRectangle);
        using var border = new Pen(Color.FromArgb(hover ? 170 : 65, accent.R, accent.G, accent.B), hover ? 1.5f : 1f);
        e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
        DrawIcon(e.Graphics, new Rectangle(0, 6, Width, 38));
        using var f = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold);
        using var b = new SolidBrush(TextColor());
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        e.Graphics.DrawString(title, f, b, new RectangleF(4, 44, Width - 8, 27), sf);
    }

    Color TextColor() => Color.FromArgb(242, 248, 255);

    void DrawIcon(Graphics g, Rectangle r)
    {
        using var pen = new Pen(accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var thin = new Pen(Color.FromArgb(145, accent.R, accent.G, accent.B), 1.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var cx = r.Left + r.Width / 2;
        var cy = r.Top + r.Height / 2;
        switch (icon)
        {
            case ActionIcon.Radar:
                g.DrawEllipse(pen, cx - 11, cy - 11, 22, 22); g.DrawEllipse(thin, cx - 6, cy - 6, 12, 12); g.DrawLine(thin, cx, cy - 16, cx, cy + 16); g.DrawLine(thin, cx - 16, cy, cx + 16, cy); g.FillEllipse(new SolidBrush(accent), cx - 3, cy - 3, 6, 6); break;
            case ActionIcon.Gamepad:
                var pad = new Rectangle(cx - 16, cy - 9, 32, 18); g.DrawArc(pen, cx - 16, cy - 9, 18, 18, 180, 180); g.DrawArc(pen, cx - 2, cy - 9, 18, 18, 180, 180); g.DrawLine(pen, cx - 10, cy, cx - 10, cy + 8); g.DrawLine(pen, cx - 14, cy + 4, cx - 6, cy + 4); g.DrawEllipse(pen, cx + 7, cy - 2, 3, 3); g.DrawEllipse(pen, cx + 13, cy - 2, 3, 3); break;
            case ActionIcon.Network:
                g.DrawLine(pen, cx, cy - 10, cx - 13, cy + 5); g.DrawLine(pen, cx, cy - 10, cx + 13, cy + 5); g.DrawLine(pen, cx - 13, cy + 5, cx + 13, cy + 5); g.FillEllipse(new SolidBrush(accent), cx - 3, cy - 13, 6, 6); g.DrawEllipse(pen, cx - 16, cy + 3, 6, 6); g.DrawEllipse(pen, cx + 10, cy + 3, 6, 6); break;
            case ActionIcon.Router:
                g.DrawRoundedRectangle(pen, new Rectangle(cx - 15, cy - 5, 30, 13), 4); g.DrawLine(pen, cx - 8, cy - 5, cx - 8, cy - 14); g.DrawLine(pen, cx + 8, cy - 5, cx + 8, cy - 14); g.DrawArc(thin, cx - 15, cy - 18, 30, 18, 210, 120); g.FillEllipse(new SolidBrush(accent), cx - 9, cy - 1, 3, 3); g.FillEllipse(new SolidBrush(accent), cx - 2, cy - 1, 3, 3); break;
            case ActionIcon.Search:
                g.DrawEllipse(pen, cx - 12, cy - 12, 20, 20); g.DrawLine(pen, cx + 4, cy + 4, cx + 15, cy + 15); break;
            case ActionIcon.Route:
                g.DrawBezier(pen, cx - 16, cy - 8, cx - 3, cy + 8, cx + 3, cy - 8, cx + 16, cy + 8); g.FillEllipse(new SolidBrush(accent), cx - 18, cy - 10, 5, 5); g.FillEllipse(new SolidBrush(accent), cx + 14, cy + 6, 5, 5); break;
            case ActionIcon.Ping:
                g.DrawEllipse(pen, cx - 12, cy - 12, 24, 24); g.DrawLine(pen, cx, cy, cx + 10, cy - 6); g.DrawLine(pen, cx, cy, cx, cy - 10); break;
            case ActionIcon.Trace:
                for (var i = -1; i <= 1; i++) { g.FillEllipse(new SolidBrush(accent), cx - 14 + i * 10, cy - 4 + i * 5, 5, 5); if (i < 1) g.DrawLine(thin, cx - 8 + i * 10, cy - 1 + i * 5, cx + 1 + i * 10, cy + 2 + i * 5); } break;
            case ActionIcon.Chart:
                g.DrawLine(pen, cx - 15, cy + 10, cx - 15, cy - 10); g.DrawLine(pen, cx - 5, cy + 10, cx - 5, cy - 2); g.DrawLine(pen, cx + 5, cy + 10, cx + 5, cy - 14); g.DrawLine(pen, cx + 15, cy + 10, cx + 15, cy - 6); break;
            case ActionIcon.Report:
                g.DrawRectangle(pen, cx - 10, cy - 14, 20, 28); g.DrawLine(thin, cx - 5, cy - 5, cx + 6, cy - 5); g.DrawLine(thin, cx - 5, cy + 1, cx + 6, cy + 1); g.DrawLine(thin, cx - 5, cy + 7, cx + 3, cy + 7); break;
        }
    }
}

sealed class StageItem : Panel
{
    readonly string number;
    readonly string title;
    readonly Color accent;
    public StageItem(string number, string title, Color accent)
    {
        this.number = number; this.title = title; this.accent = accent;
        Width = 118; Height = 58; Margin = new Padding(0, 0, 8, 0); BackColor = Color.Transparent; DoubleBuffered = true;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var cx = 59; using var p = new Pen(Color.FromArgb(75, 55, 90), 1); e.Graphics.DrawLine(p, 0, 16, Width - 1, 16);
        using var b = new SolidBrush(accent); e.Graphics.FillEllipse(b, cx - 10, 6, 20, 20);
        using var f = new Font("Segoe UI Semibold", 8, FontStyle.Bold); using var tb = new SolidBrush(Color.FromArgb(3, 8, 16)); var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; e.Graphics.DrawString(number, f, tb, new RectangleF(cx - 10, 6, 20, 20), sf);
        using var tf = new Font("Segoe UI Semibold", 7.2f, FontStyle.Bold); using var white = new SolidBrush(Color.FromArgb(230, 240, 255)); e.Graphics.DrawString(title, tf, white, new RectangleF(0, 30, Width, 25), sf);
    }
}

sealed class GameMemoryItem : Panel
{
    readonly GameProfile profile; readonly Color good, accent;
    public GameMemoryItem(GameProfile profile, Color good, Color accent) { this.profile = profile; this.good = good; this.accent = accent; BackColor = Color.FromArgb(7, 15, 29); Cursor = Cursors.Hand; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(profile.LastScore >= 70 ? good : Color.FromArgb(25, 63, 93), 1); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        using var iconBrush = new SolidBrush(Color.FromArgb(2, 7, 14)); e.Graphics.FillRectangle(iconBrush, 8, 9, 54, 54);
        try { if (File.Exists(profile.IconPath)) using var img = Image.FromFile(profile.IconPath) usingImage: e.Graphics.DrawImage(img, new Rectangle(9, 10, 52, 52)); } catch { }
        using var nameFont = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold); using var infoFont = new Font("Segoe UI", 8.1f);
        e.Graphics.DrawString(profile.DisplayName, nameFont, Brushes.White, new RectangleF(72, 8, Math.Max(80, Width - 78), 22));
        var info = $"{profile.Observations} analyses\r\nBest: {(string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint)}";
        using var infoBrush = new SolidBrush(profile.LastScore >= 70 ? good : Color.FromArgb(135, 159, 190)); e.Graphics.DrawString(info, infoFont, infoBrush, new RectangleF(72, 31, Math.Max(80, Width - 78), 38));
        using var dot = new SolidBrush(profile.LastScore >= 70 ? good : accent); e.Graphics.FillEllipse(dot, Math.Max(Width - 18, 70), 10, 7, 7);
    }
}

sealed class StatusBadge : Panel
{
    public Color Accent { get; set; } = Color.LimeGreen; public string Title { get; set; } = "SYSTEM"; public string Value { get; set; } = "READY";
    public StatusBadge() { BackColor = Color.FromArgb(5, 11, 23); DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using var p = new Pen(Color.FromArgb(90, Accent.R, Accent.G, Accent.B)); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold); using var v = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold); using var b = new SolidBrush(Color.FromArgb(135, 159, 190)); using var vb = new SolidBrush(Accent);
        e.Graphics.DrawString(Title, f, b, 14, 9); e.Graphics.DrawString("●  " + Value, v, vb, 14, 33);
    }
}

sealed class GlowProgress : Control
{
    int value;
    public float Phase { get; set; }
    public int Value { get => value; set { this.value = Math.Clamp(value, 0, 100); Invalidate(); } }
    public GlowProgress() { DoubleBuffered = true; BackColor = Color.FromArgb(2, 6, 15); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (Width < 2 || Height < 2) return; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 3, Width - 1, Math.Max(5, Height - 6));
        using var bg = new SolidBrush(Color.FromArgb(16, 25, 45)); e.Graphics.FillRectangle(bg, r);
        var w = (int)(r.Width * value / 100.0); if (w <= 0) return;
        using var g = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, w), r.Height), Color.FromArgb(163, 62, 255), Color.FromArgb(0, 224, 255), LinearGradientMode.Horizontal); e.Graphics.FillRectangle(g, new Rectangle(0, r.Y, w, r.Height));
        var glowX = (int)((Math.Sin(Phase * 2.0) * 0.5 + 0.5) * Math.Max(1, w - 1)); using var glow = new SolidBrush(Color.FromArgb(90, 255, 255, 255)); e.Graphics.FillEllipse(glow, Math.Max(0, glowX - 5), r.Y - 2, 10, r.Height + 4);
    }
}

sealed class RadarControl : Control
{
    public float Phase { get; set; }
    public RadarControl() { DoubleBuffered = true; BackColor = Color.Transparent; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var c = new Point(Width / 2, Height / 2); var radius = Math.Max(20, Math.Min(Width, Height) / 2 - 16);
        using var grid = new Pen(Color.FromArgb(105, 181, 70, 255), 1.5f); using var cross = new Pen(Color.FromArgb(70, 0, 224, 255), 1);
        for (var i = 1; i <= 3; i++) { var rr = radius * i / 3; e.Graphics.DrawEllipse(grid, c.X - rr, c.Y - rr, rr * 2, rr * 2); }
        e.Graphics.DrawLine(cross, c.X - radius, c.Y, c.X + radius, c.Y); e.Graphics.DrawLine(cross, c.X, c.Y - radius, c.X, c.Y + radius);
        var angle = Phase; var ex = c.X + (float)Math.Cos(angle) * radius; var ey = c.Y + (float)Math.Sin(angle) * radius;
        using var sweep = new Pen(Color.FromArgb(155, 181, 70, 255), 2); e.Graphics.DrawLine(sweep, c.X, c.Y, ex, ey);
        using var dot = new SolidBrush(Color.FromArgb(220, 181, 70, 255)); e.Graphics.FillEllipse(dot, c.X - 6, c.Y - 6, 12, 12);
    }
}

sealed class SparklineControl : Control
{
    public List<double> Values { get; set; } = new() { 54, 57, 53, 61, 56, 63, 59, 64, 60, 66 };
    public float Phase { get; set; }
    public SparklineControl() { DoubleBuffered = true; BackColor = Color.FromArgb(5, 11, 22); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); if (Width < 20 || Height < 20) return; e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var grid = new Pen(Color.FromArgb(22, 45, 72)); for (var y = 12; y < Height; y += 20) e.Graphics.DrawLine(grid, 0, y, Width, y);
        if (Values.Count < 2) return; var min = Values.Min(); var max = Values.Max(); var range = Math.Max(1, max - min);
        var points = Values.Select((v, i) => new Point(8 + i * Math.Max(1, (Width - 16) / Math.Max(1, Values.Count - 1)), Height - 10 - (int)((v - min) / range * Math.Max(10, Height - 24)))).ToArray();
        using var line = new Pen(Color.FromArgb(34, 240, 106), 2); e.Graphics.DrawLines(line, points); using var dot = new SolidBrush(Color.FromArgb(34, 240, 106)); foreach (var p in points) e.Graphics.FillEllipse(dot, p.X - 2, p.Y - 2, 5, 5);
        var pulse = (int)((Math.Sin(Phase * 2) + 1) * 2); using var glow = new SolidBrush(Color.FromArgb(55, 34, 240, 106)); var last = points[^1]; e.Graphics.FillEllipse(glow, last.X - 5 - pulse, last.Y - 5 - pulse, 10 + pulse * 2, 10 + pulse * 2);
    }
}

static class ModernMainFormColor
{
    public static readonly Color Purple = Color.FromArgb(181, 70, 255);
    public static readonly Color Cyan = Color.FromArgb(0, 224, 255);
}
