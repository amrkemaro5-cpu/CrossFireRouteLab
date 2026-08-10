using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed class GameRouteLabDashboard : Form
{
    static readonly Color Bg = Color.FromArgb(2, 5, 13);
    static readonly Color Surface = Color.FromArgb(6, 12, 25);
    static readonly Color Surface2 = Color.FromArgb(9, 18, 35);
    static readonly Color Cyan = Color.FromArgb(0, 224, 255);
    static readonly Color Purple = Color.FromArgb(181, 70, 255);
    static readonly Color Blue = Color.FromArgb(58, 130, 255);
    static readonly Color Green = Color.FromArgb(34, 240, 106);
    static readonly Color Yellow = Color.FromArgb(255, 207, 56);
    static readonly Color Red = Color.FromArgb(255, 74, 115);
    static readonly Color TextColor = Color.FromArgb(235, 244, 255);
    static readonly Color Muted = Color.FromArgb(135, 159, 190);

    readonly FlowLayoutPanel games = new();
    readonly RichTextBox console = new();
    readonly Label gameName = new(), gameMeta = new(), network = new(), router = new(), best = new(),
        metrics = new(), quality = new(), tips = new(), progressText = new(), systemText = new(),
        connections = new(), analysisTitle = new();
    readonly TextBox endpoint = new();
    readonly GlowProgress progress = new();
    readonly CenteredRadarControl radar = new();
    readonly SparklineControl graph = new();
    readonly List<IconButton> actions = new();
    readonly List<GameProfile> memory = new();
    GameProfile? current;
    bool busy;

    public GameRouteLabDashboard()
    {
        Text = "Game Route Lab";
        Width = 1600;
        Height = 980;
        MinimumSize = new Size(1360, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        memory.AddRange(GameProfileStore.Load());
        Build();
        RefreshMemory();

        Log("GAME ROUTE LAB v7.0");
        Log("Smart game detection • ISP • router • endpoint • route quality • local game memory");
        Log("READ-ONLY MODE: no routes, DNS, PPPoE, router settings or firmware are changed.");
    }

    void Build()
    {
        SuspendLayout();
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 3, RowCount = 1, Padding = new Padding(14, 8, 14, 8), Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 266));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 326));
        body.Controls.Add(BuildLeft(), 0, 0);
        body.Controls.Add(BuildCenter(), 1, 0);
        body.Controls.Add(BuildRight(), 2, 0);
        root.Controls.Add(body, 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);
        ResumeLayout(true);
    }

    Control BuildHeader()
    {
        var header = new NeonHeader { Dock = DockStyle.Fill, Padding = Padding.Empty };
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Padding = new Padding(28, 10, 28, 10) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 252));

        content.Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = Brand.CreateLogo(108), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Margin = Padding.Empty }, 0, 0);
        var titleStack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Padding = new Padding(8, 15, 0, 6), Margin = Padding.Empty };
        titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        titleStack.RowStyles.Add(new RowStyle(SizeType.Percent, 23));
        titleStack.Controls.Add(LabelOf("GAME ROUTE LAB", DockStyle.Fill, 31, TextColor, true, ContentAlignment.MiddleLeft), 0, 0);
        titleStack.Controls.Add(LabelOf("SMARTER ROUTES.  BETTER PING.", DockStyle.Fill, 12, Cyan, true, ContentAlignment.MiddleLeft), 0, 1);
        titleStack.Controls.Add(LabelOf("LOCAL-FIRST GAME NETWORK ANALYZER", DockStyle.Fill, 9, Muted, false, ContentAlignment.MiddleLeft), 0, 2);
        content.Controls.Add(titleStack, 1, 0);
        content.Controls.Add(new StatusBadge { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 8), Accent = Green, Title = "SYSTEM STATUS", Value = "READY • READ-ONLY" }, 2, 0);
        header.Controls.Add(content);
        return header;
    }

    Control BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(3, 7, 16), Padding = new Padding(12, 8, 12, 8) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(0, 2, 0, 2), Margin = Padding.Empty };
        var endpointBox = new TableLayoutPanel { Width = 220, Height = 78, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(2, 0, 8, 0) };
        endpointBox.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        endpointBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        endpointBox.Controls.Add(LabelOf("ENDPOINT", DockStyle.Fill, 8, Muted, true, ContentAlignment.BottomLeft), 0, 0);
        endpoint.Dock = DockStyle.Fill;
        endpoint.Margin = new Padding(0, 3, 0, 3);
        endpoint.BackColor = Surface2;
        endpoint.ForeColor = TextColor;
        endpoint.BorderStyle = BorderStyle.FixedSingle;
        endpoint.PlaceholderText = "Optional IP / hostname";
        endpointBox.Controls.Add(endpoint, 0, 1);
        flow.Controls.Add(endpointBox);
        AddAction(flow, "◎", "AUTO ANALYZE", AutoAnalyze, Purple);
        AddAction(flow, "⌁", "REFRESH GAMES", RefreshGames, Cyan);
        AddAction(flow, "◌", "DETECT NETWORK", DetectNetwork, Cyan);
        AddAction(flow, "▣", "DETECT ROUTER", DetectRouter, Purple);
        AddAction(flow, "⌕", "FIND CONNECTIONS", FindConnections, Cyan);
        AddAction(flow, "⌁", "ROUTE TABLE", RouteTable, Blue);
        AddAction(flow, "◉", "PING 30x", Ping30, Green);
        AddAction(flow, "⇢", "TRACEROUTE", Traceroute, Purple);
        AddAction(flow, "▥", "PATH QUALITY", PathQuality, Green);
        AddAction(flow, "▤", "SAVE REPORT", SaveReport, Purple);
        bar.Controls.Add(flow);
        return bar;
    }

    void AddAction(Control parent, string glyph, string title, Func<Task> action, Color accent)
    {
        var b = new IconButton(glyph, title, accent) { Width = 106, Height = 78, Margin = new Padding(3, 0, 3, 0) };
        b.Click += async (_, _) => await Safe(action);
        actions.Add(b);
        parent.Controls.Add(b);
    }

    Control BuildLeft()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));
        var memoryCard = new NeonCard { Dock = DockStyle.Fill, Accent = Purple };
        var memoryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Padding = new Padding(10, 8, 10, 10) };
        memoryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        memoryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        memoryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        memoryLayout.Controls.Add(HeaderLabel("GAME MEMORY", "YOUR LOCAL HISTORY", Purple), 0, 0);
        games.Dock = DockStyle.Fill;
        games.FlowDirection = FlowDirection.TopDown;
        games.WrapContents = false;
        games.AutoScroll = true;
        games.BackColor = Color.Transparent;
        games.Margin = Padding.Empty;
        memoryLayout.Controls.Add(games, 0, 1);
        var all = new NeonButton("⌁  VIEW ALL GAMES", Purple) { Dock = DockStyle.Fill, Margin = new Padding(2, 5, 2, 1) };
        all.Click += (_, _) => AllGames();
        memoryLayout.Controls.Add(all, 0, 2);
        memoryCard.Controls.Add(memoryLayout);
        root.Controls.Add(memoryCard, 0, 0);

        var quick = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan };
        var quickLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Padding = new Padding(10, 8, 10, 10) };
        quickLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        for (var i = 1; i < 4; i++) quickLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 33.333f));
        quickLayout.Controls.Add(LabelOf("QUICK ACTIONS", DockStyle.Fill, 12, Cyan, true, ContentAlignment.MiddleLeft), 0, 0);
        quickLayout.Controls.Add(MakeQuick("♜  CLEAR MEMORY", Purple, () => { memory.Clear(); GameProfileStoreClear(); RefreshMemory(); }), 0, 1);
        quickLayout.Controls.Add(MakeQuick("⇩  EXPORT ALL REPORTS", Cyan, () => SaveReport().GetAwaiter().GetResult()), 0, 2);
        quickLayout.Controls.Add(MakeQuick("⚙  SETTINGS", Purple, () => MessageBox.Show("Game Route Lab runs in READ-ONLY mode. Network analysis does not change router, DNS, PPPoE or Windows route settings.", "Game Route Lab", MessageBoxButtons.OK, MessageBoxIcon.Information)), 0, 3);
        quick.Controls.Add(quickLayout);
        root.Controls.Add(quick, 0, 1);
        return root;
    }

    Control MakeQuick(string text, Color accent, Action action)
    {
        var b = new NeonButton(text, accent) { Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 2) };
        b.Click += (_, _) => action();
        return b;
    }

    Control BuildCenter()
    {
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 29));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        center.Controls.Add(BuildHero(), 0, 0);
        center.Controls.Add(BuildSummary(), 0, 1);
        center.Controls.Add(BuildBest(), 0, 2);
        center.Controls.Add(BuildConsole(), 0, 3);
        return center;
    }

    Control BuildHero()
    {
        var card = new NeonCard { Dock = DockStyle.Fill, Accent = Purple };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(12, 10, 12, 8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 62));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 38));
        radar.Dock = DockStyle.Fill;
        radar.Margin = new Padding(0, 0, 8, 0);
        layout.Controls.Add(radar, 0, 0);
        layout.SetRowSpan(radar, 2);

        var heroInfo = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Color.Transparent, Padding = new Padding(4, 4, 4, 0) };
        heroInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        heroInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        heroInfo.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        heroInfo.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        analysisTitle.Text = "AUTO ANALYSIS IN PROGRESS";
        analysisTitle.Dock = DockStyle.Fill;
        analysisTitle.Font = new Font("Segoe UI Semibold", 20, FontStyle.Bold);
        analysisTitle.ForeColor = Purple;
        analysisTitle.TextAlign = ContentAlignment.MiddleLeft;
        heroInfo.Controls.Add(analysisTitle, 0, 0);
        heroInfo.Controls.Add(LabelOf("Detecting the game, connections and route quality automatically...", DockStyle.Fill, 10, Muted, false, ContentAlignment.MiddleLeft), 0, 1);
        progress.Dock = DockStyle.Fill;
        progress.Margin = new Padding(0, 2, 0, 2);
        heroInfo.Controls.Add(progress, 0, 2);
        var stages = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = false, BackColor = Color.Transparent, Padding = new Padding(0, 2, 0, 0), Margin = Padding.Empty };
        foreach (var (symbol, title, color) in new[] { ("✓", "DETECT GAME", Green), ("✓", "FIND CONNECTIONS", Green), ("✓", "TEST ENDPOINTS", Green), ("4", "ANALYZE ROUTES", Purple), ("5", "GENERATE REPORT", Muted) }) stages.Controls.Add(StageItem(symbol, title, color));
        heroInfo.Controls.Add(stages, 0, 3);
        layout.Controls.Add(heroInfo, 1, 0);
        progressText.Dock = DockStyle.Fill;
        progressText.Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold);
        progressText.ForeColor = TextColor;
        progressText.Text = "READY";
        progressText.TextAlign = ContentAlignment.MiddleCenter;
        layout.Controls.Add(progressText, 2, 0);
        layout.SetRowSpan(progressText, 2);
        card.Controls.Add(layout);
        return card;
    }

    Control StageItem(string symbol, string title, Color color)
    {
        var p = new Panel { Width = 132, Height = 46, Margin = new Padding(0, 0, 12, 0), BackColor = Color.Transparent };
        p.Controls.Add(LabelOf(symbol, new Rectangle(0, 0, 132, 22), 13, color, true, ContentAlignment.MiddleCenter));
        p.Controls.Add(LabelOf(title, new Rectangle(0, 22, 132, 20), 7.8f, color, true, ContentAlignment.MiddleCenter));
        return p;
    }

    Control BuildSummary()
    {
        var card = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(10, 7, 10, 8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(LabelOf("CURRENT ANALYSIS SUMMARY", DockStyle.Fill, 13, Cyan, true, ContentAlignment.MiddleLeft), 0, 0);
        var cols = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent };
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var active = new NeonInnerCard { Dock = DockStyle.Fill, Accent = Cyan, Margin = new Padding(2, 2, 6, 2), Padding = new Padding(14, 10, 14, 10) };
        var activeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent };
        activeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        activeLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        activeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        activeLayout.Controls.Add(LabelOf("⌁  ACTIVE GAME", DockStyle.Fill, 11, Cyan, true, ContentAlignment.MiddleLeft), 0, 0);
        gameName.Text = "No game detected";
        gameName.Dock = DockStyle.Fill;
        gameName.Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold);
        gameName.ForeColor = TextColor;
        gameName.TextAlign = ContentAlignment.MiddleLeft;
        activeLayout.Controls.Add(gameName, 0, 1);
        gameMeta.Text = "Start an online game and click AUTO ANALYZE";
        gameMeta.Dock = DockStyle.Fill;
        gameMeta.ForeColor = Muted;
        activeLayout.Controls.Add(gameMeta, 0, 2);
        active.Controls.Add(activeLayout);
        cols.Controls.Add(active, 0, 0);

        var conn = new NeonInnerCard { Dock = DockStyle.Fill, Accent = Cyan, Margin = new Padding(6, 2, 2, 2), Padding = new Padding(14, 10, 14, 10) };
        var connLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        connLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        connLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        connLayout.Controls.Add(LabelOf("⚙  CONNECTIONS DISCOVERED", DockStyle.Fill, 11, Cyan, true, ContentAlignment.MiddleLeft), 0, 0);
        connections.Text = "No endpoints discovered yet.\r\n\r\nAUTO ANALYZE will find public connections for the active game automatically.";
        connections.Dock = DockStyle.Fill;
        connections.ForeColor = Muted;
        connLayout.Controls.Add(connections, 0, 1);
        conn.Controls.Add(connLayout);
        cols.Controls.Add(conn, 1, 0);
        root.Controls.Add(cols, 0, 1);
        card.Controls.Add(root);
        return card;
    }

    Control BuildBest()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        var bestCard = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan, Margin = new Padding(0, 4, 4, 0) };
        var bestLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Padding = new Padding(14, 8, 14, 8) };
        bestLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        bestLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        bestLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bestLayout.Controls.Add(LabelOf("🏆  BEST ENDPOINT (CURRENT)", DockStyle.Fill, 12, Cyan, true, ContentAlignment.MiddleLeft), 0, 0);
        best.Text = "—";
        best.Dock = DockStyle.Fill;
        best.Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold);
        best.ForeColor = TextColor;
        best.TextAlign = ContentAlignment.MiddleLeft;
        bestLayout.Controls.Add(best, 0, 1);
        metrics.Text = "LATENCY     — ms\r\nLOSS        —\r\nJITTER      — ms\r\nSTABILITY   —";
        metrics.Dock = DockStyle.Fill;
        metrics.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        metrics.ForeColor = Green;
        bestLayout.Controls.Add(metrics, 0, 2);
        bestCard.Controls.Add(bestLayout);
        row.Controls.Add(bestCard, 0, 0);

        var qualityCard = new NeonCard { Dock = DockStyle.Fill, Accent = Green, Margin = new Padding(4, 4, 0, 0) };
        var q = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Color.Transparent, Padding = new Padding(14, 8, 14, 8) };
        q.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        q.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        q.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        var qHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.Transparent };
        qHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        qHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        qHeader.Controls.Add(LabelOf("ROUTE QUALITY", DockStyle.Fill, 12, Cyan, true, ContentAlignment.MiddleLeft), 0, 0);
        quality.Text = "WAITING";
        quality.Dock = DockStyle.Fill;
        quality.ForeColor = Muted;
        quality.Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold);
        quality.TextAlign = ContentAlignment.MiddleRight;
        qHeader.Controls.Add(quality, 1, 0);
        q.Controls.Add(qHeader, 0, 0);
        graph.Dock = DockStyle.Fill;
        graph.Margin = new Padding(0, 2, 0, 2);
        q.Controls.Add(graph, 0, 1);
        q.Controls.Add(LabelOf("Hops: —   |   Avg Latency: —", DockStyle.Fill, 8.8f, Muted, false, ContentAlignment.MiddleLeft), 0, 2);
        qualityCard.Controls.Add(q);
        row.Controls.Add(qualityCard, 1, 0);
        return row;
    }

    Control BuildConsole()
    {
        var p = new NeonCard { Dock = DockStyle.Fill, Accent = Blue, Margin = new Padding(0, 4, 0, 0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(10, 5, 10, 7) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(LabelOf("LIVE ANALYSIS CONSOLE", DockStyle.Fill, 10.5f, Cyan, true, ContentAlignment.MiddleLeft), 0, 0);
        console.Dock = DockStyle.Fill;
        console.BackColor = Color.FromArgb(1, 4, 10);
        console.ForeColor = TextColor;
        console.BorderStyle = BorderStyle.None;
        console.Font = new Font("Cascadia Mono", 8.6f);
        console.ReadOnly = true;
        console.WordWrap = false;
        console.Margin = Padding.Empty;
        layout.Controls.Add(console, 0, 1);
        p.Controls.Add(layout);
        return p;
    }

    Control BuildRight()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        root.Controls.Add(InfoCard("◌  NETWORK INFORMATION", Cyan, network, "ISP\t—\r\nASN\t—\r\nPublic IP\t—\r\nLocation\t—\r\nConnection\t—\r\nDNS\t—"), 0, 0);
        root.Controls.Add(InfoCard("▣  ROUTER INTELLIGENCE", Purple, router, "Gateway\t—\r\nManufacturer\t—\r\nModel\t—\r\nFirmware\t—\r\nInterface\t—\r\nConfidence\t—"), 0, 1);
        root.Controls.Add(InfoCard("◉  TIPS", Purple, tips, "Run analysis while the game is in an online match.\r\n\r\nMore observations = better local memory.\r\n\r\nICMP-blocked servers are not automatically treated as packet loss.\r\n\r\nThe analyzer never changes your router or Windows routes."), 0, 2);
        return root;
    }

    Control InfoCard(string title, Color accent, Label target, string initial)
    {
        var card = new NeonCard { Dock = DockStyle.Fill, Accent = accent, Margin = new Padding(0, 0, 0, 4) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Padding = new Padding(12, 9, 12, 9) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(LabelOf(title, DockStyle.Fill, 11.5f, accent, true, ContentAlignment.MiddleLeft), 0, 0);
        target.Text = initial;
        target.Dock = DockStyle.Fill;
        target.ForeColor = target == tips ? Muted : TextColor;
        target.Font = new Font("Segoe UI", 9.2f);
        layout.Controls.Add(target, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    Control BuildFooter()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(2, 6, 15), Padding = new Padding(18, 0, 18, 0) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        grid.Controls.Add(LabelOf("◷  Game Route Lab v7.0   •   READ-ONLY MODE", DockStyle.Fill, 9, Green, true, ContentAlignment.MiddleLeft), 0, 0);
        systemText.Text = "▣  System: Windows 64-bit";
        systemText.Dock = DockStyle.Fill;
        systemText.ForeColor = Cyan;
        systemText.TextAlign = ContentAlignment.MiddleCenter;
        grid.Controls.Add(systemText, 1, 0);
        grid.Controls.Add(LabelOf("●  READY", DockStyle.Fill, 9, Green, true, ContentAlignment.MiddleRight), 2, 0);
        p.Controls.Add(grid);
        return p;
    }

    Control HeaderLabel(string title, string subtitle, Color accent)
    {
        var p = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = Padding.Empty };
        p.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        p.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        p.Controls.Add(LabelOf(title, DockStyle.Fill, 13, accent, true, ContentAlignment.MiddleLeft), 0, 0);
        p.Controls.Add(LabelOf(subtitle, DockStyle.Fill, 8, Muted, true, ContentAlignment.MiddleLeft), 0, 1);
        return p;
    }

    Label LabelOf(string text, DockStyle dock, float font, Color color, bool bold, ContentAlignment align) => new()
    {
        Text = text, Dock = dock, Font = new Font("Segoe UI", font, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color,
        BackColor = Color.Transparent, TextAlign = align, AutoEllipsis = true, Margin = Padding.Empty
    };

    Label LabelOf(string text, Rectangle bounds, float font, Color color, bool bold, ContentAlignment align) => new()
    {
        Text = text, Bounds = bounds, Font = new Font("Segoe UI", font, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color,
        BackColor = Color.Transparent, TextAlign = align, AutoEllipsis = true, Margin = Padding.Empty
    };

    async Task Safe(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        foreach (var a in actions) a.Enabled = false;
        SetProgress(5, "5%");
        try { await action(); }
        catch (Exception ex) { Log("[ERROR] " + ex.Message); quality.Text = "ERROR"; quality.ForeColor = Red; }
        finally { busy = false; foreach (var a in actions) a.Enabled = true; if (progress.Value < 100) SetProgress(100, "READY"); }
    }

    void SetProgress(int value, string text) { progress.Value = Math.Clamp(value, 0, 100); progressText.Text = text; radar.Invalidate(); }

    async Task AutoAnalyze()
    {
        analysisTitle.Text = "AUTO ANALYSIS IN PROGRESS"; quality.Text = "ANALYZING"; quality.ForeColor = Purple; SetProgress(8, "8%");
        Log("\r\n============================================================\r\nAUTO ANALYSIS STARTED\r\n============================================================");
        await DetectNetwork(); SetProgress(22, "22%"); await DetectRouter(); SetProgress(38, "38%");
        var gamesFound = await DiscoverGames();
        if (gamesFound.Count == 0) { gameName.Text = "No high-confidence game detected"; gameMeta.Text = "Start an online game and run AUTO ANALYZE again."; connections.Text = "No public game endpoints found yet."; Log("STOP: no high-confidence game was detected. Normal applications are not guessed as games."); quality.Text = "WAITING"; quality.ForeColor = Muted; return; }
        current = gamesFound[0]; gameName.Text = current.DisplayName; gameMeta.Text = $"{current.Observations} saved analyses\r\nPath: {current.ExecutablePath}\r\nLast best: {(string.IsNullOrWhiteSpace(current.LastBestEndpoint) ? "—" : current.LastBestEndpoint)}"; SetProgress(48, "48%"); Log($"GAME: {current.DisplayName} | executable identified | saved memory loaded");
        var live = (await GameScanner.DiscoverAsync()).FirstOrDefault(x => x.ExecutablePath.Equals(current.ExecutablePath, StringComparison.OrdinalIgnoreCase) || x.ProcessName.Equals(current.ProcessName, StringComparison.OrdinalIgnoreCase));
        var endpoints = live == null ? new List<GameEndpoint>() : await GetEndpoints(live.Pid);
        connections.Text = endpoints.Count == 0 ? "Game detected, but no public established sockets are visible.\r\n\r\nEnter an online match and retry." : string.Join("\r\n", endpoints.Take(5).Select(x => $"{x.Protocol}  {x.RemoteIp}:{x.RemotePort}   {x.State}")) + (endpoints.Count > 5 ? $"\r\n… and {endpoints.Count - 5} more" : "");
        Log($"FOUND {endpoints.Count} candidate game endpoint(s). Testing automatically — no IP copying required."); if (endpoints.Count == 0) return;
        SetProgress(62, "62%"); var results = new List<RouteResult>();
        foreach (var ep in endpoints.Take(8)) { Log($"TESTING {ep.RemoteIp}:{ep.RemotePort}/{ep.Protocol} ..."); results.Add(new RouteResult(ep, await Probe(ep.RemoteIp), await Trace(ep.RemoteIp))); }
        var bestRoute = results.OrderBy(x => x.Score).First(); ApplyResult(bestRoute); GameProfileStore.Record(current, $"{bestRoute.Endpoint.RemoteIp}:{bestRoute.Endpoint.RemotePort}", Math.Max(0, 100 - bestRoute.Score), $"hops={bestRoute.Trace.Hops}; last={bestRoute.Trace.Last:0}ms");
        memory.Clear(); memory.AddRange(GameProfileStore.Load()); current = memory.FirstOrDefault(x => x.Key == current.Key) ?? current; RefreshMemory(); SetProgress(100, "100%"); analysisTitle.Text = "ANALYSIS COMPLETE";
        Log($"BEST: {bestRoute.Endpoint.RemoteIp}:{bestRoute.Endpoint.RemotePort} | {bestRoute.Probe.Avg:0} ms | loss {bestRoute.Probe.Loss:0}% | hops {bestRoute.Trace.Hops}"); Log("ICMP timeout is treated as blocked/unknown evidence, not automatic game packet loss.");
    }

    void ApplyResult(RouteResult result)
    {
        best.Text = $"{result.Endpoint.RemoteIp}:{result.Endpoint.RemotePort}   ({result.Endpoint.Protocol})";
        metrics.Text = $"LATENCY     {(result.Probe.Avg > 0 ? result.Probe.Avg.ToString("0") : "—")} ms\r\nLOSS        {(result.Probe.HasResponse ? result.Probe.Loss.ToString("0") : "unknown")}\r\nJITTER      {(result.Probe.HasResponse ? result.Probe.Jitter.ToString("0") : "—")} ms\r\nSTABILITY   {result.Stability}";
        quality.Text = result.Stability.ToUpperInvariant(); quality.ForeColor = result.Stability == "Excellent" ? Green : result.Stability == "Good" ? Yellow : result.Stability == "Unknown" ? Muted : Red; graph.Values = result.Probe.History.Count > 0 ? result.Probe.History : new List<double> { 1 }; graph.Invalidate();
    }

    async Task RefreshGames() { var found = await DiscoverGames(); Log($"\r\n=== GAME DISCOVERY: {found.Count} candidate(s) ==="); foreach (var g in found) Log($"{g.DisplayName} | {g.ProcessName} | {g.Observations} saved observations | {g.ExecutablePath}"); }

    async Task<List<GameProfile>> DiscoverGames()
    {
        var items = await GameScanner.DiscoverAsync();
        var candidates = items.Where(x => x.LikelyGame).GroupBy(x => new { x.Pid, x.ProcessName, x.ExecutablePath }).Select(g => g.OrderByDescending(x => x.Confidence).First()).OrderByDescending(x => x.Confidence).Take(12).ToList();
        var profiles = new List<GameProfile>();
        foreach (var g in candidates) profiles.Add(GameProfileStore.Touch(g.ProcessName, g.ExecutablePath));
        memory.Clear(); memory.AddRange(GameProfileStore.Load()); RefreshMemory(); return profiles;
    }

    async Task DetectNetwork()
    {
        var n = await NetworkProfileDetector.DetectAsync();
        network.Text = $"ISP\t{n.ISP}\r\nASN\t{n.ASN}\r\nPublic IP\t{n.PublicIp}\r\nLocation\t{n.City}, {n.Country}\r\nConnection\t{n.WanType}\r\nDNS\t{n.DnsServers}";
        systemText.Text = $"▣  System: Windows {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}   •   {n.InterfaceName}";
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
        var found = await GameScanner.DiscoverAsync(); var game = found.FirstOrDefault(x => x.LikelyGame);
        if (game == null) { Log("No high-confidence game connection found."); return; }
        var eps = await GetEndpoints(game.Pid);
        connections.Text = eps.Count == 0 ? "No public established sockets visible." : string.Join("\r\n", eps.Select(x => $"{x.Protocol}  {x.RemoteIp}:{x.RemotePort}   {x.State}"));
        foreach (var ep in eps) Log($"{ep.Protocol} {ep.RemoteIp}:{ep.RemotePort} {ep.State}");
    }

    async Task RouteTable() => Log("\r\n=== ROUTE TABLE ===\r\n" + await Run("route.exe", "print", 10000));
    async Task Ping30() { var ip = Target(); if (ip.Length == 0) { Log("No endpoint selected."); return; } Log($"\r\n=== PING 30x {ip} ===\r\n" + await Run("ping.exe", $"-n 30 {ip}", 50000)); }
    async Task Traceroute() { var ip = Target(); if (ip.Length == 0) { Log("No endpoint selected."); return; } Log($"\r\n=== TRACEROUTE {ip} ===\r\n" + await Run("tracert.exe", $"-d -h 30 -w 700 {ip}", 45000)); }

    async Task PathQuality()
    {
        var ip = Target(); if (ip.Length == 0) { Log("No endpoint selected."); return; }
        var p = await Probe(ip); var t = await Trace(ip); var ep = new GameEndpoint("manual", 0, "TCP", ip, 0, "MANUAL", false, 0, "");
        ApplyResult(new RouteResult(ep, p, t));
        Log($"PATH QUALITY: {(p.HasResponse ? p.Avg.ToString("0") : "blocked/unknown")} ms | loss {(p.HasResponse ? p.Loss.ToString("0") : "unknown")} | jitter {(p.HasResponse ? p.Jitter.ToString("0") : "unknown")} | hops {t.Hops}");
    }

    async Task SaveReport()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"GameRouteLab_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var text = console.Text + Environment.NewLine + "=== CURRENT RESULT ===" + Environment.NewLine + best.Text + Environment.NewLine + metrics.Text;
        await File.WriteAllTextAsync(file, text); Log("Report saved: " + file);
    }

    string Target()
    {
        if (!string.IsNullOrWhiteSpace(endpoint.Text)) return endpoint.Text.Trim();
        if (string.IsNullOrWhiteSpace(current?.LastBestEndpoint)) return "";
        var s = current.LastBestEndpoint; var k = s.LastIndexOf(':'); return k > 0 ? s[..k] : s;
    }

    async Task<List<GameEndpoint>> GetEndpoints(int pid)
    {
        var all = await GameScanner.DiscoverAsync();
        return all.Where(x => x.Pid == pid && x.LikelyGame).GroupBy(x => $"{x.Protocol}|{x.RemoteIp}|{x.RemotePort}").Select(g => g.First()).ToList();
    }

    async Task<ProbeResult> Probe(string host)
    {
        var samples = new List<long>();
        for (var i = 0; i < 5; i++) { try { using var p = new Ping(); var r = await p.SendPingAsync(host, 900); if (r.Status == IPStatus.Success) samples.Add(r.RoundtripTime); } catch { } }
        if (samples.Count == 0) return new ProbeResult(0, 0, 0, new List<double>(), false);
        var avg = samples.Average(); var jitter = samples.Count < 2 ? 0 : samples.Zip(samples.Skip(1), (a, b) => Math.Abs(a - b)).Average();
        return new ProbeResult(avg, (5 - samples.Count) * 20, jitter, samples.Select(x => (double)x).ToList(), true);
    }

    async Task<TraceResult> Trace(string host)
    {
        var text = await Run("tracert.exe", $"-d -h 18 -w 500 {host}", 24000); var hops = 0; var last = 0.0;
        foreach (var line in text.Split('\n')) { if (!Regex.IsMatch(line.TrimStart(), @"^\d+\s+")) continue; hops++; var ms = Regex.Matches(line, @"(\d+)\s*ms"); if (ms.Count > 0 && double.TryParse(ms[^1].Groups[1].Value, out var value)) last = value; }
        return new TraceResult(hops, last);
    }

    async Task<string> Run(string file, string args, int timeout)
    {
        try
        {
            using var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            p.Start(); var outputTask = p.StandardOutput.ReadToEndAsync(); var errorTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout)) { try { p.Kill(true); } catch { } return await outputTask + Environment.NewLine + await errorTask + Environment.NewLine + "[Timed out]"; }
            return await outputTask + Environment.NewLine + await errorTask;
        }
        catch (Exception ex) { return "[Command error] " + ex.Message; }
    }

    void RefreshMemory()
    {
        games.SuspendLayout(); games.Controls.Clear();
        foreach (var g in memory.OrderByDescending(x => x.LastSeenUtc).Take(8))
        {
            var item = new GameMemoryItem(g, Green, Cyan) { Width = Math.Max(205, games.ClientSize.Width - 8), Height = 76, Margin = new Padding(1, 2, 1, 6), Anchor = AnchorStyles.Left | AnchorStyles.Right };
            item.Click += (_, _) => SelectGame(g); games.Controls.Add(item);
        }
        if (memory.Count == 0) games.Controls.Add(LabelOf("No games remembered yet.\r\n\r\nStart a game and click\r\nAUTO ANALYZE.", DockStyle.Top, 9.5f, Muted, false, ContentAlignment.TopLeft));
        games.ResumeLayout(true);
    }

    void SelectGame(GameProfile profile)
    {
        current = profile; gameName.Text = profile.DisplayName; gameMeta.Text = $"{profile.Observations} saved analyses\r\nPath: {profile.ExecutablePath}\r\nBest: {(string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint)}";
        best.Text = string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint;
        Log($"\r\n[MEMORY] {profile.DisplayName}\r\nObservations: {profile.Observations}\r\nLast best: {profile.LastBestEndpoint}");
    }

    void AllGames()
    {
        using var f = new Form { Text = "Game Route Lab • All Games", Size = new Size(760, 560), BackColor = Bg, StartPosition = FormStartPosition.CenterParent, ForeColor = TextColor };
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = TextColor, Font = new Font("Segoe UI", 11), BorderStyle = BorderStyle.None };
        foreach (var g in memory) list.Items.Add($"{g.DisplayName}    •    {g.Observations} analyses    •    Best {g.LastBestEndpoint}");
        f.Controls.Add(list); f.ShowDialog(this);
    }

    void GameProfileStoreClear()
    {
        try { var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab"); var file = Path.Combine(root, "profiles.json"); if (File.Exists(file)) File.Delete(file); } catch { }
    }

    void Log(string text)
    {
        if (console.InvokeRequired) { console.BeginInvoke(() => Log(text)); return; }
        console.AppendText(text + Environment.NewLine); console.SelectionStart = console.TextLength; console.ScrollToCaret();
    }

    record ProbeResult(double Avg, double Loss, double Jitter, List<double> History, bool HasResponse);
    record TraceResult(int Hops, double Last);
    record RouteResult(GameEndpoint Endpoint, ProbeResult Probe, TraceResult Trace)
    {
        public double Score => Probe.HasResponse ? Math.Min(200, Probe.Avg + Probe.Loss * 2 + Trace.Hops * 0.5 + Probe.Jitter * 0.35) : 80 + Trace.Hops * 0.5;
        public string Stability => !Probe.HasResponse ? "Unknown" : Probe.Loss == 0 && Probe.Avg < 80 && Probe.Jitter < 12 ? "Excellent" : Probe.Loss < 20 && Probe.Avg < 150 ? "Good" : "Variable";
    }
}

sealed class CenteredRadarControl : Control
{
    public CenteredRadarControl() { DoubleBuffered = true; BackColor = Color.Transparent; MinimumSize = new Size(110, 110); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var size = Math.Min(ClientSize.Width, ClientSize.Height); var center = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f); var outer = Math.Max(24, size * 0.43f);
        using var purple = new Pen(Color.FromArgb(120, 181, 70, 255), 2); using var cyan = new Pen(Color.FromArgb(100, 0, 224, 255), 1);
        for (var i = 1; i <= 3; i++) { var d = outer * 2f * i / 3f; e.Graphics.DrawEllipse(purple, center.X - d / 2, center.Y - d / 2, d, d); }
        e.Graphics.DrawLine(cyan, center.X, center.Y - outer, center.X, center.Y + outer); e.Graphics.DrawLine(cyan, center.X - outer, center.Y, center.X + outer, center.Y);
        using var glow = new SolidBrush(Color.FromArgb(55, 181, 70, 255)); e.Graphics.FillEllipse(glow, center.X - 13, center.Y - 13, 26, 26);
        using var dot = new SolidBrush(Color.FromArgb(205, 181, 70, 255)); e.Graphics.FillEllipse(dot, center.X - 5, center.Y - 5, 10, 10);
    }
}
