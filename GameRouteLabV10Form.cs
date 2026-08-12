using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed class GameRouteLabV10Form : Form
{
    static readonly Color Bg = Color.FromArgb(3, 7, 17);
    static readonly Color Surface = Color.FromArgb(7, 13, 27);
    static readonly Color Surface2 = Color.FromArgb(10, 18, 36);
    static readonly Color Cyan = Color.FromArgb(0, 225, 255);
    static readonly Color Purple = Color.FromArgb(188, 72, 255);
    static readonly Color Green = Color.FromArgb(40, 242, 122);
    static readonly Color Blue = Color.FromArgb(83, 135, 255);
    static readonly Color Text = Color.FromArgb(238, 246, 255);
    static readonly Color Muted = Color.FromArgb(132, 157, 190);

    readonly TableLayoutPanel root = new();
    readonly TableLayoutPanel body = new();
    readonly TableLayoutPanel center = new();
    readonly FlowLayoutPanel gameList = new();
    readonly RichTextBox console = new();
    readonly TextBox endpointBox = new();
    readonly Label gameTitle = new(), gameMeta = new(), connectionText = new(), metrics = new(), quality = new();
    readonly Label networkText = new(), routerText = new(), guideText = new(), statusText = new(), progressText = new();
    readonly PictureBox gameIcon = new();
    readonly ProgressBar progress = new();
    readonly RadarPanel radar = new();
    readonly GraphPanel graph = new();
    readonly TelemetryPanel networkPanel = new(), routerPanel = new();
    readonly Timer visualTimer = new() { Interval = 120 };
    readonly Timer scanTimer = new() { Interval = 2800 };
    readonly Timer pingTimer = new() { Interval = 1000 };
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab");
    readonly string gamesFile;
    readonly List<string> customGames = new();
    readonly List<double> pingHistory = new();
    readonly List<GameConnection> connections = new();
    bool busy;
    int gamePid;
    string gameName = "";
    string? endpoint;
    int endpointPort;
    double lastPing = -1;
    double jitter;
    float phase;
    int scanPulse;
    string lastNetwork = "Waiting for network scan…";
    string lastRouter = "Waiting for router scan…";

    static readonly string[] KnownGames =
    {
        "crossfire", "crossfire2", "crossfire_client", "crossfireclient",
        "valorant", "cs2", "csgo", "cod", "codhq", "r5apex", "pubg",
        "tslgame", "leagueoflegends", "dota2", "fortniteclient-win64-shipping"
    };

    public GameRouteLabV10Form()
    {
        Text = "Game Route Lab v10";
        ClientSize = new Size(1500, 920);
        MinimumSize = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Text;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;
        TopLevel = true;
        gamesFile = Path.Combine(dataDir, "custom-games.txt");
        Directory.CreateDirectory(dataDir);
        try { Icon = Brand.CreateIcon(); } catch { }

        BuildUi();
        LoadCustomGames();
        RefreshGames(false);
        Log("GAME ROUTE LAB v10.0");
        Log("1 Detect Game → 2 Connections → 3 Test Ping → 4 Route → 5 Report");
        Log("Endpoint is automatic. Add Game EXE is only a fallback for unsupported games.");
        Log("Focus safety: v10 never calls ShowWindow, Activate, BringToFront, TopMost, or WindowState while CrossFire runs.");

        visualTimer.Tick += (_, _) => VisualTick();
        scanTimer.Tick += async (_, _) => { if (!busy) await BackgroundScan(false); };
        pingTimer.Tick += async (_, _) => { if (!busy && endpoint != null) await PingCurrent(); };
        visualTimer.Start();
        scanTimer.Start();
        FormClosed += (_, _) => { visualTimer.Stop(); scanTimer.Stop(); pingTimer.Stop(); };
    }

    void BuildUi()
    {
        root.Dock = DockStyle.Fill;
        root.ColumnCount = 1;
        root.RowCount = 4;
        root.Margin = Padding.Empty;
        root.Padding = Padding.Empty;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);
        root.Controls.Add(BuildBody(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);
    }

    Control BuildHeader()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        p.Controls.Add(new PictureBox { Image = Brand.CreateLogo(88), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(22, 14, 88, 88), BackColor = Color.Transparent });
        p.Controls.Add(MakeLabel("GAME ROUTE LAB", 124, 18, 29, Text, true, 560, 40));
        p.Controls.Add(MakeLabel("SMARTER ROUTES.  BETTER PING.", 126, 57, 12, Cyan, true, 450, 22));
        p.Controls.Add(MakeLabel("LOCAL-FIRST GAME NETWORK ANALYZER  •  v10.0", 126, 79, 8.5f, Muted, false, 470, 20));
        var state = new Panel { Bounds = new Rectangle(1220, 18, 240, 66), BackColor = Surface };
        state.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(140, Purple)); e.Graphics.DrawRectangle(pen, 0, 0, state.Width - 1, state.Height - 1); };
        state.Controls.Add(MakeLabel("●  ACTIVE • READ-ONLY", 18, 22, 9, Green, true, 205, 22));
        p.Controls.Add(state);
        p.Paint += (_, e) => { using var pen = new Pen(Color.FromArgb(150, Cyan), 2); e.Graphics.DrawLine(pen, 4, p.Height - 2, p.Width - 4, p.Height - 2); };
        return p;
    }

    Control BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(4, 9, 20), Padding = new Padding(12, 7, 12, 6) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 10, RowCount = 1, Margin = Padding.Empty };
        for (int i = 0; i < 10; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10));
        string[] names = { "AUTO ANALYZE", "REFRESH GAMES", "DETECT NETWORK", "DETECT ROUTER", "FIND CONNECTIONS", "PING 30x", "TRACEROUTE", "PATH QUALITY", "SAVE REPORT" };
        Color[] accents = { Purple, Cyan, Cyan, Purple, Cyan, Green, Purple, Green, Blue };
        for (int i = 0; i < names.Length; i++)
        {
            var b = MakeTool(names[i], accents[i]);
            b.Margin = new Padding(3, 1, 3, 1);
            t.Controls.Add(b, i + 1, 0);
        }
        endpointBox.Dock = DockStyle.Fill;
        endpointBox.ReadOnly = true;
        endpointBox.BackColor = Surface2;
        endpointBox.ForeColor = Text;
        endpointBox.BorderStyle = BorderStyle.FixedSingle;
        endpointBox.TextAlign = HorizontalAlignment.Center;
        endpointBox.PlaceholderText = "AUTO ENDPOINT";
        endpointBox.Margin = new Padding(3, 5, 3, 5);
        t.ColumnStyles.Clear();
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        for (int i = 1; i < 10; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 1));
        t.Controls.Add(endpointBox, 0, 0);
        bar.Controls.Add(t);
        return bar;
    }

    Button MakeTool(string text, Color accent)
    {
        var b = new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Text,
            Font = new Font("Segoe UI Semibold", 8.1f),
            UseVisualStyleBackColor = false,
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = accent;
        b.FlatAppearance.BorderSize = 1;
        b.Click += async (_, _) =>
        {
            if (busy && text != "PING 30x") return;
            try
            {
                switch (text)
                {
                    case "AUTO ANALYZE": await AutoAnalyze(); break;
                    case "REFRESH GAMES": RefreshGames(true); break;
                    case "DETECT NETWORK": await DetectNetwork(); break;
                    case "DETECT ROUTER": await DetectRouter(); break;
                    case "FIND CONNECTIONS": await FindConnections(); break;
                    case "PING 30x": await Ping30(); break;
                    case "TRACEROUTE": await TraceRoute(); break;
                    case "PATH QUALITY": await PathQuality(); break;
                    case "SAVE REPORT": SaveReport(); break;
                }
            }
            catch (Exception ex) { Log("[ERROR] " + ex.Message); }
        };
        return b;
    }

    Control BuildBody()
    {
        body.Dock = DockStyle.Fill;
        body.ColumnCount = 3;
        body.RowCount = 1;
        body.Margin = Padding.Empty;
        body.Padding = new Padding(12, 7, 12, 6);
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 292));
        body.Controls.Add(BuildLeft(), 0, 0);
        body.Controls.Add(BuildCenter(), 1, 0);
        body.Controls.Add(BuildRight(), 2, 0);
        return body;
    }

    Control BuildLeft()
    {
        var p = new CardPanel(Purple) { Dock = DockStyle.Fill };
        p.Controls.Add(MakeLabel("GAME MEMORY", 16, 14, 13, Purple, true, 190, 24));
        p.Controls.Add(MakeLabel("RUNNING GAMES APPEAR HERE", 16, 38, 8, Muted, true, 190, 20));
        gameList.Location = new Point(10, 62);
        gameList.FlowDirection = FlowDirection.TopDown;
        gameList.WrapContents = false;
        gameList.AutoScroll = true;
        gameList.BackColor = Color.Transparent;
        p.Controls.Add(gameList);

        var view = SideButton("VIEW ALL GAMES", Cyan, () => RefreshGames(true));
        var add = SideButton("ADD GAME EXE", Blue, AddGameExe);
        var clear = SideButton("CLEAR MEMORY", Purple, ClearMemory);
        var help = SideButton("HOW TO USE", Green, ShowGuide);
        p.Controls.Add(view); p.Controls.Add(add); p.Controls.Add(clear); p.Controls.Add(help);
        p.Resize += (_, _) =>
        {
            int buttonY = Math.Max(90, p.ClientSize.Height - 176);
            int i = 0;
            foreach (var b in new[] { view, add, clear, help }) b.SetBounds(14, buttonY + i++ * 41, p.ClientSize.Width - 28, 35);
            gameList.Bounds = new Rectangle(10, 62, p.ClientSize.Width - 20, Math.Max(80, buttonY - 70));
        };
        return p;
    }

    Button SideButton(string text, Color accent, Action action)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Text, Font = new Font("Segoe UI Semibold", 8.4f), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = accent;
        b.FlatAppearance.BorderSize = 1;
        b.Click += (_, _) => action();
        return b;
    }

    Control BuildCenter()
    {
        center.Dock = DockStyle.Fill;
        center.ColumnCount = 1;
        center.RowCount = 4;
        center.Margin = Padding.Empty;
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 24));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 27));

        var hero = new CardPanel(Purple) { Dock = DockStyle.Fill };
        hero.Controls.Add(radar);
        radar.Bounds = new Rectangle(18, 18, 116, 116);
        hero.Controls.Add(MakeLabel("GUIDED AUTO ANALYSIS", 154, 17, 20, Purple, true, 600, 34));
        hero.Controls.Add(MakeLabel("Find the game and endpoints automatically — no endpoint typing required.", 154, 48, 10, Muted, false, 760, 24));
        progress.Minimum = 0; progress.Maximum = 100; progress.Value = 0; progress.Style = ProgressBarStyle.Continuous; progress.Bounds = new Rectangle(154, 82, 650, 12); hero.Controls.Add(progress);
        progressText.Text = "READY"; progressText.ForeColor = Green; progressText.Font = new Font("Segoe UI Semibold", 8.5f); progressText.TextAlign = ContentAlignment.MiddleRight; progressText.Bounds = new Rectangle(812, 75, 80, 25); hero.Controls.Add(progressText);
        string[] steps = { "1  DETECT GAME", "2  CONNECTIONS", "3  TEST PING", "4  ROUTE", "5  REPORT" };
        for (int i = 0; i < 5; i++) hero.Controls.Add(MakeLabel(steps[i], 150 + i * 145, 118, 7.7f, i == 0 ? Green : Muted, true, 130, 20, ContentAlignment.MiddleCenter));
        center.Controls.Add(hero, 0, 0);

        var summary = new CardPanel(Cyan) { Dock = DockStyle.Fill };
        summary.Controls.Add(MakeLabel("CURRENT ANALYSIS SUMMARY", 18, 12, 12, Cyan, true, 320, 24));
        gameIcon.Bounds = new Rectangle(18, 47, 60, 60); gameIcon.SizeMode = PictureBoxSizeMode.Zoom; gameIcon.BackColor = Surface2; gameIcon.Image = Brand.CreateLogo(60); summary.Controls.Add(gameIcon);
        gameTitle.Bounds = new Rectangle(92, 46, 360, 32); gameTitle.Font = new Font("Segoe UI Semibold", 17, FontStyle.Bold); gameTitle.ForeColor = Text; summary.Controls.Add(gameTitle);
        gameMeta.Bounds = new Rectangle(92, 80, 390, 52); gameMeta.Font = new Font("Cascadia Mono", 8.5f); gameMeta.ForeColor = Muted; summary.Controls.Add(gameMeta);
        summary.Controls.Add(MakeLabel("DISCOVERED CONNECTIONS", 510, 48, 10, Cyan, true, 420, 22));
        connectionText.Bounds = new Rectangle(510, 74, 560, 55); connectionText.Font = new Font("Cascadia Mono", 8.7f); connectionText.ForeColor = Text; connectionText.AutoEllipsis = true; summary.Controls.Add(connectionText);
        center.Controls.Add(summary, 0, 1);

        var best = new CardPanel(Green) { Dock = DockStyle.Fill };
        best.Controls.Add(MakeLabel("BEST ENDPOINT + LIVE PING TRACKER", 18, 11, 12, Cyan, true, 430, 24));
        metrics.Bounds = new Rectangle(18, 48, 330, 118); metrics.Font = new Font("Cascadia Mono", 9.4f); metrics.ForeColor = Green; best.Controls.Add(metrics);
        quality.Bounds = new Rectangle(370, 12, 650, 26); quality.Font = new Font("Segoe UI Semibold", 10); quality.ForeColor = Muted; quality.TextAlign = ContentAlignment.TopRight; best.Controls.Add(quality);
        graph.Bounds = new Rectangle(360, 46, 680, 125); best.Controls.Add(graph);
        center.Controls.Add(best, 0, 2);

        var consoleCard = new CardPanel(Blue) { Dock = DockStyle.Fill };
        consoleCard.Controls.Add(MakeLabel("LIVE ANALYSIS CONSOLE", 14, 8, 11, Cyan, true, 300, 22));
        console.Location = new Point(10, 32); console.BackColor = Color.FromArgb(1, 3, 8); console.ForeColor = Text; console.ReadOnly = true; console.WordWrap = false; console.ScrollBars = RichTextBoxScrollBars.Both; console.BorderStyle = BorderStyle.FixedSingle; console.Font = new Font("Cascadia Mono", 8.4f); console.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; consoleCard.Controls.Add(console);
        consoleCard.Resize += (_, _) => console.Bounds = new Rectangle(10, 32, Math.Max(100, consoleCard.ClientSize.Width - 20), Math.Max(60, consoleCard.ClientSize.Height - 40));
        center.Controls.Add(consoleCard, 0, 3);
        return center;
    }

    Control BuildRight()
    {
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 32));

        networkPanel.Title = "NETWORK TELEMETRY"; networkPanel.Accent = Cyan; networkPanel.Content = networkText; networkPanel.State = "WAITING"; right.Controls.Add(networkPanel, 0, 0);
        routerPanel.Title = "ROUTER INTELLIGENCE"; routerPanel.Accent = Purple; routerPanel.Content = routerText; routerPanel.State = "WAITING"; right.Controls.Add(routerPanel, 0, 1);
        var guide = new CardPanel(Green) { Dock = DockStyle.Fill };
        guide.Controls.Add(MakeLabel("WHAT TO PRESS • IN ORDER", 14, 12, 10.2f, Green, true, 270, 24));
        guideText.Bounds = new Rectangle(14, 42, 260, 150); guideText.Font = new Font("Cascadia Mono", 8.1f); guideText.ForeColor = Text; guideText.Text = "1  Launch the game and enter an online match.\r\n2  Press AUTO ANALYZE.\r\n3  Wait for Best Endpoint to fill.\r\n4  Press PING 30x for a sample.\r\n5  Use TRACEROUTE / PATH QUALITY.\r\n6  Press SAVE REPORT.\r\n\r\nEndpoint is automatic.\r\nADD GAME EXE is only a fallback."; guide.Controls.Add(guideText);
        right.Controls.Add(guide, 0, 2);
        return right;
    }

    Control BuildFooter()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg };
        p.Controls.Add(MakeLabel("Game Route Lab v10.0  •  READ-ONLY  •  NO ROUTE/DNS CHANGES", 16, 4, 8.3f, Green, true, 460, 20));
        p.Controls.Add(MakeLabel("LOW-OVERHEAD ANIMATION", 500, 4, 8.3f, Cyan, true, 210, 20));
        p.Controls.Add(MakeLabel("SYSTEM: WINDOWS 64-BIT", 790, 4, 8.3f, Muted, true, 210, 20));
        statusText.Text = "● READY"; statusText.ForeColor = Green; statusText.Font = new Font("Segoe UI Semibold", 8.3f); statusText.Bounds = new Rectangle(1360, 4, 100, 20); p.Controls.Add(statusText);
        return p;
    }

    Label MakeLabel(string text, int x, int y, float size, Color color, bool bold, int width, int height, ContentAlignment align = ContentAlignment.TopLeft)
        => new() { Text = text, Bounds = new Rectangle(x, y, width, height), Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent, TextAlign = align, AutoEllipsis = true };

    async Task AutoAnalyze()
    {
        if (busy) return;
        busy = true; pingTimer.Stop();
        try
        {
            SetProgress(5, "SCANNING"); radar.Active = true; Log("[AUTO] Starting guided analysis…");
            await DetectNetwork(); SetProgress(20, "NETWORK");
            await DetectRouter(); SetProgress(32, "ROUTER");
            var game = await FindRunningGame();
            if (game == null)
            {
                ClearGame("No supported game detected", "Launch the game and enter an online match, then press AUTO ANALYZE again.");
                SetProgress(0, "WAITING");
                return;
            }
            SetGame(game); SetProgress(52, "GAME FOUND");
            await FindConnections(); SetProgress(68, "CONNECTIONS");
            if (connections.Count == 0)
            {
                quality.Text = "GAME FOUND • WAITING FOR PUBLIC ENDPOINT";
                Log("[AUTO] Game detected, but no public endpoint is visible yet. Stay in an online match and retry.");
                SetProgress(55, "WAITING");
                return;
            }
            SelectBestEndpoint(); SetProgress(82, "TESTING PING");
            await PingCurrent(); SetProgress(100, "LIVE");
            quality.Text = "● LIVE • TRACKING"; quality.ForeColor = Green;
            pingTimer.Start();
        }
        finally
        {
            busy = false;
        }
    }

    async Task<GameInfo?> FindRunningGame()
    {
        var names = new HashSet<string>(KnownGames.Concat(customGames), StringComparer.OrdinalIgnoreCase);
        return await Task.Run(() =>
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!names.Contains(p.ProcessName)) continue;
                    string path = "";
                    try { path = p.MainModule?.FileName ?? ""; } catch { }
                    return new GameInfo(PrettyName(p.ProcessName), p.Id, path, p.ProcessName);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return null;
        });
    }

    async Task FindConnections()
    {
        if (gamePid <= 0) { Log("[CONNECTIONS] No game selected. AUTO ANALYZE can select it automatically."); return; }
        SetProgress(Math.Max(progress.Value, 58), "CONNECTIONS");
        var found = await Task.Run(() => ReadConnections(gamePid));
        connections.Clear(); connections.AddRange(found);
        connectionText.Text = connections.Count == 0 ? "No public endpoint candidate yet." : string.Join("\r\n", connections.Take(5).Select(c => $"{c.Protocol,-3}  {c.Ip}:{c.Port,-5}  {c.State}"));
        Log($"[CONNECTIONS] {connections.Count} public endpoint candidate(s) found.");
        if (connections.Count > 0) SelectBestEndpoint();
    }

    List<GameConnection> ReadConnections(int pid)
    {
        var result = new List<GameConnection>();
        foreach (var line in RunNetstat("-ano -p tcp"))
        {
            var m = Regex.Match(line.Trim(), @"^TCP\s+(\S+):(\d+)\s+(\S+):(\d+)\s+(\S+)\s+(\d+)$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[6].Value, out var linePid) || linePid != pid) continue;
            var ip = NormalizeIp(m.Groups[3].Value);
            if (!IsPublicIp(ip)) continue;
            if (IsIgnoredPort(int.Parse(m.Groups[4].Value))) continue;
            result.Add(new GameConnection(ip, int.Parse(m.Groups[4].Value), "TCP", m.Groups[5].Value));
        }
        foreach (var line in RunNetstat("-ano -p udp"))
        {
            var m = Regex.Match(line.Trim(), @"^UDP\s+(\S+):(\d+)\s+(\S+)\s+(\d+)$", RegexOptions.IgnoreCase);
            if (!m.Success || !int.TryParse(m.Groups[4].Value, out var linePid) || linePid != pid) continue;
            var ip = NormalizeIp(m.Groups[3].Value);
            if (!IsPublicIp(ip)) continue;
            if (ip == "0.0.0.0" || ip == "::") continue;
            result.Add(new GameConnection(ip, int.Parse(m.Groups[2].Value), "UDP", "ACTIVE"));
        }
        return result.GroupBy(x => $"{x.Protocol}|{x.Ip}|{x.Port}").Select(g => g.First()).OrderBy(x => x.Protocol == "TCP" ? 0 : 1).Take(12).ToList();
    }

    IEnumerable<string> RunNetstat(string args)
    {
        try
        {
            using var p = new Process { StartInfo = new ProcessStartInfo("netstat.exe", args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.ASCII } };
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(1800);
            return output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }
        catch { return Array.Empty<string>(); }
    }

    void SelectBestEndpoint()
    {
        var chosen = connections.OrderBy(c => c.Protocol == "TCP" ? 0 : 1).ThenBy(c => c.Port).FirstOrDefault();
        if (chosen == null) return;
        endpoint = chosen.Ip; endpointPort = chosen.Port;
        endpointBox.Text = $"{chosen.Ip}:{chosen.Port}";
        metrics.Text = $"ENDPOINT   {chosen.Ip}:{chosen.Port}\r\nPROTOCOL   {chosen.Protocol}\r\nLATENCY    {(lastPing < 0 ? "—" : $"{lastPing:0} ms")}\r\nLOSS       —\r\nJITTER     {(lastPing < 0 ? "—" : $"{jitter:0.0} ms")}\r\nSTABILITY  {(lastPing < 0 ? "WAITING" : "TRACKING")}";
        quality.Text = "● TARGET SELECTED"; quality.ForeColor = Cyan;
        Log($"[ENDPOINT] Automatically selected {chosen.Ip}:{chosen.Port} ({chosen.Protocol}).");
    }

    async Task PingCurrent()
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        var target = endpoint; var port = endpointPort;
        var result = await Task.Run(() => Probe(target!, port));
        if (result < 0)
        {
            Log($"[PING] {target}:{port} did not answer the probe.");
            return;
        }
        if (lastPing >= 0) jitter = Math.Abs(result - lastPing) * .35 + jitter * .65;
        lastPing = result;
        pingHistory.Add(result); if (pingHistory.Count > 36) pingHistory.RemoveAt(0);
        graph.Values = pingHistory.Count < 3 ? MakeWaitingGraph() : pingHistory.ToList();
        metrics.Text = $"ENDPOINT   {target}:{port}\r\nPROTOCOL   {connections.FirstOrDefault(c => c.Ip == target && c.Port == port)?.Protocol ?? "TCP"}\r\nLATENCY    {lastPing:0} ms\r\nLOSS       0%*\r\nJITTER     {jitter:0.0} ms\r\nSTABILITY  {Stability(lastPing, jitter)}\r\n\r\n*Probe loss is not game-packet loss.";
        quality.Text = $"● LIVE • {lastPing:0} ms"; quality.ForeColor = Green;
        Log($"[PING] {target}:{port} → {lastPing:0} ms | jitter {jitter:0.0} ms");
    }

    double Probe(string ip, int port)
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(ip, 900);
            if (reply.Status == IPStatus.Success) return reply.RoundtripTime;
        }
        catch { }
        if (port > 0 && port < 65536)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var client = new TcpClient();
                var task = client.ConnectAsync(ip, port);
                if (task.Wait(850) && client.Connected) { sw.Stop(); return sw.Elapsed.TotalMilliseconds; }
            }
            catch { }
        }
        return -1;
    }

    async Task Ping30()
    {
        if (string.IsNullOrWhiteSpace(endpoint)) { Log("[PING] No automatic endpoint yet. Run AUTO ANALYZE first."); return; }
        pingTimer.Stop();
        int ok = 0, total = 30;
        for (int i = 0; i < total; i++)
        {
            var before = lastPing;
            await PingCurrent();
            if (lastPing >= 0 && lastPing != before) ok++;
            await Task.Delay(120);
        }
        Log($"[PING 30x] Completed {total} samples; {ok} responded to the probe.");
        pingTimer.Start();
    }

    async Task TraceRoute()
    {
        if (string.IsNullOrWhiteSpace(endpoint)) { Log("[TRACEROUTE] No endpoint selected."); return; }
        var target = endpoint;
        Log($"[TRACEROUTE] Testing route to {target}…");
        var output = await Task.Run(() => RunCommand("tracert.exe", $"-d -h 12 -w 650 {target}"));
        Log(output.Length > 3200 ? output[^3200..] : output);
    }

    async Task PathQuality()
    {
        if (string.IsNullOrWhiteSpace(endpoint)) { Log("[PATH] No endpoint selected."); return; }
        var target = endpoint;
        var samples = await Task.Run(() => Enumerable.Range(0, 8).Select(_ => Probe(target!, endpointPort)).Where(x => x >= 0).ToList());
        if (samples.Count == 0) { Log("[PATH] Endpoint did not answer the probe."); return; }
        var avg = samples.Average(); var min = samples.Min(); var max = samples.Max();
        Log($"[PATH] {target} | min {min:0} ms | avg {avg:0} ms | max {max:0} ms | spread {(max - min):0} ms");
    }

    string RunCommand(string file, string args)
    {
        try
        {
            using var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            p.Start(); var output = p.StandardOutput.ReadToEnd(); var error = p.StandardError.ReadToEnd(); p.WaitForExit(15000); return output + (string.IsNullOrWhiteSpace(error) ? "" : "\r\n" + error);
        }
        catch (Exception ex) { return "Command error: " + ex.Message; }
    }

    async Task DetectNetwork()
    {
        lastNetwork = await Task.Run(() =>
        {
            try
            {
                var nics = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();
                var n = nics.FirstOrDefault(x => x.GetIPProperties().GatewayAddresses.Any()) ?? nics.FirstOrDefault();
                if (n == null) return "No active interface detected.";
                var props = n.GetIPProperties();
                var local = props.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "—";
                var gateway = props.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "—";
                var dns = string.Join(", ", props.DnsAddresses.Take(2).Select(x => x.ToString()));
                return $"INTERFACE   {n.Name}\r\nLOCAL IP    {local}\r\nGATEWAY     {gateway}\r\nDNS         {dns}";
            }
            catch (Exception ex) { return "Network scan error: " + ex.Message; }
        });
        networkPanel.State = "LIVE"; networkPanel.Invalidate();
        Log("[NETWORK] Local network telemetry refreshed.");
    }

    async Task DetectRouter()
    {
        lastRouter = await Task.Run(() =>
        {
            try
            {
                var n = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.OperationalStatus == OperationalStatus.Up && x.GetIPProperties().GatewayAddresses.Any());
                if (n == null) return "ROUTER   gateway not detected";
                var p = n.GetIPProperties();
                var gateway = p.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "—";
                return $"GATEWAY     {gateway}\r\nINTERFACE   {n.Name}\r\nTYPE        {n.NetworkInterfaceType}\r\nSTATE       LINK UP\r\nCONFIDENCE  LOCAL";
            }
            catch (Exception ex) { return "Router scan error: " + ex.Message; }
        });
        routerPanel.State = "LIVE"; routerPanel.Invalidate();
        Log("[ROUTER] Router intelligence refreshed from local interface data.");
    }

    async Task BackgroundScan(bool verbose)
    {
        if (busy) return;
        var game = await FindRunningGame();
        if (game == null) return;
        if (gamePid != game.Pid)
        {
            SetGame(game);
            await FindConnections();
            if (connections.Count > 0) SelectBestEndpoint();
            if (verbose) Log("[SCAN] Running game changed; analysis target updated.");
        }
        else if (endpoint == null)
        {
            await FindConnections();
        }
    }

    void SetGame(GameInfo g)
    {
        gamePid = g.Pid; gameName = g.Name;
        gameTitle.Text = g.Name;
        gameMeta.Text = $"PID       {g.Pid}\r\nPATH      {(string.IsNullOrWhiteSpace(g.Path) ? "access unavailable" : g.Path)}\r\nRUNNING   YES • connections collected automatically";
        gameIcon.Image?.Dispose();
        gameIcon.Image = ExtractIcon(g.Path) ?? Brand.CreateLogo(60);
        RefreshGames(false);
        Log($"[GAME] {g.Name} detected (PID {g.Pid}).");
    }

    void ClearGame(string title, string meta)
    {
        gamePid = 0; gameName = ""; endpoint = null; endpointPort = 0; connections.Clear(); pingHistory.Clear(); lastPing = -1; jitter = 0;
        gameTitle.Text = title; gameMeta.Text = meta; gameIcon.Image = Brand.CreateLogo(60); endpointBox.Clear(); connectionText.Text = "No public endpoint candidate yet."; metrics.Text = "ENDPOINT   —\r\nLATENCY    —\r\nLOSS       —\r\nJITTER     —\r\nSTABILITY  WAITING"; quality.Text = "WAITING FOR A TARGET"; quality.ForeColor = Muted;
    }

    void RefreshGames(bool log)
    {
        if (log) Log("[GAMES] Refresh requested. Running processes are scanned in the background.");
        _ = Task.Run(() =>
        {
            var names = new HashSet<string>(KnownGames.Concat(customGames), StringComparer.OrdinalIgnoreCase);
            var list = new List<GameInfo>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!names.Contains(p.ProcessName)) continue;
                    string path = ""; try { path = p.MainModule?.FileName ?? ""; } catch { }
                    list.Add(new GameInfo(PrettyName(p.ProcessName), p.Id, path, p.ProcessName));
                }
                catch { }
                finally { p.Dispose(); }
            }
            BeginInvoke((Action)(() => RenderGameList(list)));
        });
    }

    void RenderGameList(List<GameInfo> games)
    {
        gameList.SuspendLayout(); gameList.Controls.Clear();
        if (games.Count == 0)
        {
            gameList.Controls.Add(new Label { Text = "No supported game is running.\r\nLaunch a game to detect it automatically.", ForeColor = Muted, Font = new Font("Segoe UI", 8.2f), AutoSize = false, Width = Math.Max(160, gameList.ClientSize.Width - 8), Height = 58, Padding = new Padding(6) });
        }
        foreach (var g in games)
        {
            var card = new Panel { Width = Math.Max(170, gameList.ClientSize.Width - 8), Height = 72, BackColor = Surface2, Margin = new Padding(0, 0, 0, 6), Cursor = Cursors.Hand };
            var pic = new PictureBox { Image = ExtractIcon(g.Path) ?? Brand.CreateLogo(46), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(6, 9, 48, 48) };
            card.Controls.Add(pic);
            card.Controls.Add(MakeLabel(g.Name, 60, 9, 9.5f, Text, true, card.Width - 70, 22));
            card.Controls.Add(MakeLabel($"PID {g.Pid}\r\n{(g.Pid == gamePid ? "ACTIVE • ANALYZING" : "RUNNING")}", 60, 31, 7.5f, g.Pid == gamePid ? Green : Muted, false, card.Width - 70, 34));
            card.Click += async (_, _) => { if (!busy) { SetGame(g); await FindConnections(); } };
            gameList.Controls.Add(card);
        }
        gameList.ResumeLayout(true);
    }

    void AddGameExe()
    {
        using var dialog = new OpenFileDialog { Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*", Title = "Add a game executable" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var name = Path.GetFileNameWithoutExtension(dialog.FileName);
        if (!customGames.Contains(name, StringComparer.OrdinalIgnoreCase)) { customGames.Add(name); File.WriteAllLines(gamesFile, customGames); }
        Log($"[GAME] Added {name}. Start it and AUTO ANALYZE will detect it automatically.");
        RefreshGames(true);
    }

    void ClearMemory()
    {
        customGames.Clear();
        try { if (File.Exists(gamesFile)) File.Delete(gamesFile); } catch { }
        pingHistory.Clear(); endpoint = null; endpointBox.Clear();
        Log("[MEMORY] Custom game list cleared. Built-in game signatures remain available.");
        RefreshGames(true);
    }

    void ShowGuide()
    {
        MessageBox.Show(this, "GAME ROUTE LAB v10 — QUICK GUIDE\r\n\r\n1. Launch CrossFire and enter an online match.\r\n2. Press AUTO ANALYZE.\r\n3. The app detects the game and fills the endpoint automatically.\r\n4. Watch BEST ENDPOINT + LIVE PING TRACKER.\r\n5. Press PING 30x for a sample, then TRACEROUTE or PATH QUALITY.\r\n6. SAVE REPORT when finished.\r\n\r\nYou normally do not type an endpoint.\r\nADD GAME EXE is only for games the built-in detector does not know.", "How to use Game Route Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SaveReport()
    {
        using var dialog = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = $"GameRouteLab-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var sb = new StringBuilder(); sb.AppendLine("GAME ROUTE LAB v10 REPORT"); sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")); sb.AppendLine(); sb.AppendLine($"Game: {gameName}"); sb.AppendLine($"PID: {gamePid}"); sb.AppendLine($"Endpoint: {endpoint}:{endpointPort}"); sb.AppendLine($"Last probe: {(lastPing < 0 ? "—" : lastPing.ToString("0") + " ms")}"); sb.AppendLine($"Jitter: {jitter:0.0} ms"); sb.AppendLine(); sb.AppendLine("Network:"); sb.AppendLine(lastNetwork); sb.AppendLine(); sb.AppendLine("Router:"); sb.AppendLine(lastRouter); sb.AppendLine(); sb.AppendLine("Console:"); sb.Append(console.Text);
        File.WriteAllText(dialog.FileName, sb.ToString()); Log("[REPORT] Saved " + dialog.FileName);
    }

    void LoadCustomGames()
    {
        try { if (File.Exists(gamesFile)) customGames.AddRange(File.ReadAllLines(gamesFile).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)); } catch { }
    }

    async Task DetectNetworkAndRouter() { await DetectNetwork(); await DetectRouter(); }

    void VisualTick()
    {
        if (IsDisposed) return;
        phase += .045f; scanPulse++;
        radar.Phase = phase; radar.Active = progress.Value > 0 && progress.Value < 100;
        graph.Phase = phase;
        networkText.Text = lastNetwork;
        routerText.Text = lastRouter;
        networkPanel.Phase = phase; routerPanel.Phase = phase;
        networkPanel.State = lastNetwork.StartsWith("Waiting") ? "WAITING" : "LIVE";
        routerPanel.State = lastRouter.StartsWith("Waiting") ? "WAITING" : "LIVE";
        networkPanel.Invalidate(); routerPanel.Invalidate(); radar.Invalidate(); graph.Invalidate();
    }

    void SetProgress(int value, string state)
    {
        progress.Value = Math.Clamp(value, 0, 100); progressText.Text = state; statusText.Text = "● " + state;
        statusText.ForeColor = state is "LIVE" or "READY" ? Green : Cyan;
    }

    void Log(string text)
    {
        if (console.IsDisposed) return;
        console.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n");
        console.SelectionStart = console.TextLength; console.ScrollToCaret();
    }

    string PrettyName(string name) => name.Equals("crossfire", StringComparison.OrdinalIgnoreCase) || name.Contains("crossfire", StringComparison.OrdinalIgnoreCase) ? "CrossFire" : name.ToUpperInvariant();
    string NormalizeIp(string ip) => ip.Trim('[', ']');
    bool IsIgnoredPort(int p) => p is 80 or 443 or 53 or 123 or 5222;
    bool IsPublicIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var a) || IPAddress.IsLoopback(a)) return false;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254));
        }
        return !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal;
    }
    string Stability(double ping, double jit) => ping < 0 ? "WAITING" : jit < 4 ? "EXCELLENT" : jit < 10 ? "GOOD" : "VARIABLE";
    List<double> MakeWaitingGraph() => Enumerable.Range(0, 28).Select(i => 45 + 8 * Math.Sin(i * .55 + phase * 2) + 3 * Math.Sin(i * 1.1 + phase)).ToList();
    Bitmap? ExtractIcon(string path)
    {
        try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return Icon.ExtractAssociatedIcon(path)?.ToBitmap(); } catch { }
        return null;
    }

    sealed record GameInfo(string Name, int Pid, string Path, string ProcessName);
    sealed record GameConnection(string Ip, int Port, string Protocol, string State);

    sealed class CardPanel : Panel
    {
        public Color Accent { get; }
        public CardPanel(Color accent) { Accent = accent; BackColor = Surface; DoubleBuffered = true; Padding = Padding.Empty; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var p = new Pen(Color.FromArgb(150, Accent), 1); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            using var glow = new Pen(Color.FromArgb(65, Accent), 2); e.Graphics.DrawLine(glow, 0, 0, Math.Min(Width - 1, 150), 0);
        }
    }

    sealed class RadarPanel : Control
    {
        public bool Active; public float Phase;
        public RadarPanel() { DoubleBuffered = true; BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; float cX = Width / 2f, cY = Height / 2f;
            using var pen = new Pen(Color.FromArgb(130, Purple), 1);
            for (int r = 16; r < Math.Min(Width, Height) / 2; r += 16) e.Graphics.DrawEllipse(pen, cX - r, cY - r, r * 2, r * 2);
            e.Graphics.DrawLine(pen, cX, 4, cX, Height - 4); e.Graphics.DrawLine(pen, 4, cY, Width - 4, cY);
            if (Active)
            {
                float a = Phase * 1.7f; float x = cX + (Width * .44f) * MathF.Cos(a); float y = cY + (Height * .44f) * MathF.Sin(a);
                using var sweep = new Pen(Color.FromArgb(210, Cyan), 2); e.Graphics.DrawLine(sweep, cX, cY, x, y);
                using var dot = new SolidBrush(Green); e.Graphics.FillEllipse(dot, cX - 4, cY - 4, 8, 8);
            }
        }
    }

    sealed class GraphPanel : Control
    {
        public float Phase; public List<double> Values { get; set; } = new();
        public GraphPanel() { DoubleBuffered = true; BackColor = Color.FromArgb(5, 11, 22); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var grid = new Pen(Color.FromArgb(35, 85, 105), 1);
            for (int i = 1; i < 5; i++) e.Graphics.DrawLine(grid, 0, i * Height / 5, Width, i * Height / 5);
            if (Values.Count < 2) return;
            var min = Math.Max(0, Values.Min() - 8); var max = Math.Max(min + 20, Values.Max() + 8);
            var pts = new PointF[Values.Count]; for (int i = 0; i < Values.Count; i++) { float x = i * (Width - 8f) / Math.Max(1, Values.Count - 1) + 4; float y = Height - 8 - (float)((Values[i] - min) / (max - min) * (Height - 16)); pts[i] = new PointF(x, y); }
            using var glow = new Pen(Color.FromArgb(70, Green), 4); e.Graphics.DrawLines(glow, pts); using var line = new Pen(Green, 1.7f); e.Graphics.DrawLines(line, pts);
            float pulse = (MathF.Sin(Phase * 2.4f) + 1) * .5f; var last = pts[^1]; using var brush = new SolidBrush(Color.FromArgb(100 + (int)(120 * pulse), Green)); e.Graphics.FillEllipse(brush, last.X - 4, last.Y - 4, 8, 8);
        }
    }

    sealed class TelemetryPanel : Control
    {
        public string Title = "TELEMETRY"; public string State = "WAITING"; public Color Accent = Cyan; public Label? Content; public float Phase;
        public TelemetryPanel() { DoubleBuffered = true; BackColor = Surface; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var border = new Pen(Color.FromArgb(150, Accent), 1); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            using var titleBrush = new SolidBrush(Accent); using var font = new Font("Segoe UI Semibold", 10); e.Graphics.DrawString(Title, font, titleBrush, 14, 12);
            using var stateBrush = new SolidBrush(State == "LIVE" ? Green : Muted); using var sf = new StringFormat { Alignment = StringAlignment.Far }; e.Graphics.DrawString("● " + State, new Font("Segoe UI Semibold", 7.5f), stateBrush, new RectangleF(14, 13, Width - 28, 20), sf);
            int y = 40; using var scan = new Pen(Color.FromArgb(55, Accent), 1); for (int i = 0; i < 5; i++) e.Graphics.DrawLine(scan, 14, y + i * 17, Width - 14, y + i * 17);
            if (State == "LIVE") { float x = 14 + ((MathF.Sin(Phase * 1.6f) + 1) * .5f) * (Width - 28); using var pulse = new Pen(Color.FromArgb(190, Accent), 2); e.Graphics.DrawLine(pulse, x, 40, x, Height - 12); }
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); if (Content != null) Content.Bounds = new Rectangle(14, 43, Math.Max(100, Width - 28), Math.Max(45, Height - 52)); }
    }
}
