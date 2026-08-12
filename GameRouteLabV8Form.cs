using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

/// <summary>
/// Game Route Lab v8: a lightweight, read-only network analyzer dashboard.
/// This form intentionally does not use the old CrossFire window guard and never
/// changes Windows routes, DNS, router settings, or game settings.
/// </summary>
public sealed class GameRouteLabV8Form : Form
{
    static readonly Color Bg = Color.FromArgb(3, 6, 15);
    static readonly Color Panel = Color.FromArgb(7, 12, 25);
    static readonly Color Panel2 = Color.FromArgb(10, 17, 33);
    static readonly Color Cyan = Color.FromArgb(0, 231, 255);
    static readonly Color Purple = Color.FromArgb(185, 74, 255);
    static readonly Color Blue = Color.FromArgb(86, 137, 255);
    static readonly Color Green = Color.FromArgb(37, 242, 116);
    static readonly Color Yellow = Color.FromArgb(255, 207, 64);
    static readonly Color Red = Color.FromArgb(255, 83, 120);
    static readonly Color Text = Color.FromArgb(239, 246, 255);
    static readonly Color Muted = Color.FromArgb(133, 158, 190);

    readonly FlowLayoutPanel memoryPanel = new();
    readonly RichTextBox console = new();
    readonly Label gameTitle = new(), gameDetails = new(), endpointTitle = new(), metricLabel = new(), qualityLabel = new(), networkLabel = new(), routerLabel = new(), guideLabel = new(), statusLabel = new();
    readonly ProgressBar progress = new();
    readonly V8Radar radar = new();
    readonly V8Sparkline spark = new();
    readonly V8Header header = new();
    readonly TextBox endpointBox = new();
    readonly List<GameTarget> targets = new();
    readonly List<Button> toolButtons = new();
    readonly System.Windows.Forms.Timer uiTimer = new() { Interval = 100 };
    readonly System.Windows.Forms.Timer scanTimer = new() { Interval = 3000 };
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab");
    readonly string gamesFile;
    string? selectedGame;
    int selectedPid;
    string? bestIp;
    int bestPort;
    int lastLatency = -1;
    float phase;
    bool scanning;
    bool autoScan = true;

    static readonly string[] KnownProcessNames =
    {
        "crossfire", "crossfire2", "crossfire_client", "crossfireclient",
        "valorant-win64-shipping", "valorant", "cs2", "csgo",
        "cod", "cod2", "modernwarfare", "r5apex", "apex_legends",
        "pubg", "tslgame", "leagueoflegends", "dota2", "fortniteclient-win64-shipping"
    };

    public GameRouteLabV8Form()
    {
        Text = "Game Route Lab v8";
        ClientSize = new Size(1500, 920);
        MinimumSize = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = Text;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        gamesFile = Path.Combine(dataDir, "custom-games.txt");
        Directory.CreateDirectory(dataDir);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        BuildUi();
        LoadCustomGames();
        RefreshGames(false);
        Log("GAME ROUTE LAB v8.0");
        Log("Guided mode: 1 Detect Game → 2 Find Connections → 3 Test Endpoints → 4 Analyze Route → 5 Report");
        Log("READ-ONLY: the analyzer does not change Windows routes, DNS, PPPoE, router settings or game files.");
        uiTimer.Tick += (_, _) => Animate();
        scanTimer.Tick += async (_, _) => { if (autoScan && !scanning) await AutoAnalyzeAsync(true); };
        uiTimer.Start();
        scanTimer.Start();
        FormClosed += (_, _) => { uiTimer.Stop(); scanTimer.Stop(); };
    }

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Bg, Margin = Padding.Empty, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 88));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);
        root.Controls.Add(BuildBody(), 0, 2);
        root.Controls.Add(BuildFooter(), 0, 3);
        Controls.Add(root);
    }

    Control BuildHeader()
    {
        header.Dock = DockStyle.Fill;
        header.Title = "GAME ROUTE LAB";
        header.Subtitle = "SMARTER ROUTES.  BETTER PING.";
        header.Tagline = "LOCAL-FIRST GAME NETWORK ANALYZER  •  v8.0";
        header.Resize += (_, _) => { header.StatusLocation = new Point(Math.Max(720, header.ClientSize.Width - 270), 25); };
        return header;
    }

    Control BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(4, 8, 18), Padding = new Padding(12, 7, 12, 6) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, BackColor = Color.Transparent, Margin = Padding.Empty };
        flow.Controls.Add(MakeText("ENDPOINT", 60, 70, 8, Muted, true, ContentAlignment.MiddleLeft));
        endpointBox.Width = 180; endpointBox.Height = 34; endpointBox.Margin = new Padding(0, 18, 8, 0); endpointBox.BackColor = Panel2; endpointBox.ForeColor = Text; endpointBox.BorderStyle = BorderStyle.FixedSingle; endpointBox.PlaceholderText = "Optional IP / hostname"; flow.Controls.Add(endpointBox);
        AddTool(flow, "AUTO ANALYZE", Purple, async () => await AutoAnalyzeAsync(false));
        AddTool(flow, "REFRESH GAMES", Cyan, () => { RefreshGames(true); return Task.CompletedTask; });
        AddTool(flow, "ADD GAME", Blue, () => { AddGame(); return Task.CompletedTask; });
        AddTool(flow, "DETECT NETWORK", Cyan, () => { DetectNetwork(); return Task.CompletedTask; });
        AddTool(flow, "DETECT ROUTER", Purple, () => { DetectRouter(); return Task.CompletedTask; });
        AddTool(flow, "FIND CONNECTIONS", Cyan, async () => { await FindConnectionsAsync(); });
        AddTool(flow, "PING 30x", Green, async () => { await PingBestAsync(); });
        AddTool(flow, "TRACEROUTE", Purple, async () => { await TraceAsync(); });
        AddTool(flow, "PATH QUALITY", Green, async () => { await PathQualityAsync(); });
        AddTool(flow, "SAVE REPORT", Blue, () => { SaveReport(); return Task.CompletedTask; });
        bar.Controls.Add(flow);
        return bar;
    }

    Control BuildBody()
    {
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Bg, Padding = new Padding(12, 8, 12, 7), Margin = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
        body.Controls.Add(BuildLeft(), 0, 0);
        body.Controls.Add(BuildCenter(), 1, 0);
        body.Controls.Add(BuildRight(), 2, 0);
        return body;
    }

    Control BuildLeft()
    {
        var card = new V8Card { Dock = DockStyle.Fill, Accent = Purple };
        card.Controls.Add(MakeText("GAME MEMORY", 16, 14, 13, Purple, true));
        card.Controls.Add(MakeText("RUNNING GAMES APPEAR HERE", 16, 39, 8, Muted, true));
        memoryPanel.Location = new Point(10, 66); memoryPanel.Size = new Size(230, 470); memoryPanel.FlowDirection = FlowDirection.TopDown; memoryPanel.WrapContents = false; memoryPanel.AutoScroll = true; memoryPanel.BackColor = Color.Transparent; memoryPanel.Margin = Padding.Empty; card.Controls.Add(memoryPanel);
        var view = MakeButton("VIEW ALL GAMES", Cyan, 16, 548, 218, 38); view.Click += (_, _) => RefreshGames(true); card.Controls.Add(view);
        var add = MakeButton("＋ ADD GAME EXE", Blue, 16, 594, 218, 38); add.Click += (_, _) => AddGame(); card.Controls.Add(add);
        var clear = MakeButton("CLEAR MEMORY", Purple, 16, 640, 218, 38); clear.Click += (_, _) => ClearMemory(); card.Controls.Add(clear);
        var guide = MakeButton("HOW TO USE", Green, 16, 686, 218, 38); guide.Click += (_, _) => ShowGuide(); card.Controls.Add(guide);
        return card;
    }

    Control BuildCenter()
    {
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Bg, Margin = Padding.Empty };
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var hero = new V8Card { Dock = DockStyle.Fill, Accent = Purple };
        radar.Size = new Size(150, 150); radar.Location = new Point(18, 18); hero.Controls.Add(radar);
        hero.Controls.Add(MakeText("GUIDED AUTO ANALYSIS", 188, 18, 22, Purple, true));
        hero.Controls.Add(MakeText("The app finds the game and endpoints for you. You do not need to type an endpoint.", 190, 54, 10, Muted));
        progress.Location = new Point(190, 88); progress.Size = new Size(720, 12); progress.Style = ProgressBarStyle.Continuous; progress.Maximum = 100; hero.Controls.Add(progress);
        statusLabel.Text = "READY"; statusLabel.Location = new Point(925, 80); statusLabel.Size = new Size(100, 28); statusLabel.TextAlign = ContentAlignment.MiddleRight; statusLabel.Font = new Font("Segoe UI Semibold", 9); statusLabel.ForeColor = Green; hero.Controls.Add(statusLabel);
        var stepNames = new[] { "1  DETECT GAME", "2  CONNECTIONS", "3  TEST PING", "4  ROUTE", "5  REPORT" };
        for (int i = 0; i < stepNames.Length; i++) hero.Controls.Add(MakeText(stepNames[i], 188 + i * 160, 132, 8, i == 0 ? Green : Muted, true, ContentAlignment.MiddleCenter, 120, 28));
        center.Controls.Add(hero, 0, 0);

        var summary = new V8Card { Dock = DockStyle.Fill, Accent = Cyan };
        summary.Controls.Add(MakeText("CURRENT ANALYSIS SUMMARY", 18, 13, 12, Cyan, true));
        gameTitle.Text = "No game detected"; gameTitle.Location = new Point(20, 50); gameTitle.Size = new Size(520, 34); gameTitle.Font = new Font("Segoe UI Semibold", 18, FontStyle.Bold); gameTitle.ForeColor = Text; summary.Controls.Add(gameTitle);
        gameDetails.Text = "Launch the game, enter an online match, then press AUTO ANALYZE.\r\nIf automatic detection misses it, use ADD GAME EXE once."; gameDetails.Location = new Point(20, 88); gameDetails.Size = new Size(520, 60); gameDetails.ForeColor = Muted; summary.Controls.Add(gameDetails);
        summary.Controls.Add(MakeText("DISCOVERED CONNECTIONS", 560, 50, 10, Cyan, true));
        endpointTitle.Text = "No endpoint selected"; endpointTitle.Location = new Point(560, 78); endpointTitle.Size = new Size(450, 30); endpointTitle.Font = new Font("Cascadia Mono", 10); endpointTitle.ForeColor = Text; summary.Controls.Add(endpointTitle);
        metricLabel.Text = "TCP/UDP connections will be tested automatically."; metricLabel.Location = new Point(560, 112); metricLabel.Size = new Size(450, 40); metricLabel.Font = new Font("Cascadia Mono", 8.5f); metricLabel.ForeColor = Muted; summary.Controls.Add(metricLabel);
        center.Controls.Add(summary, 0, 1);

        var result = new V8Card { Dock = DockStyle.Fill, Accent = Green };
        result.Controls.Add(MakeText("BEST ENDPOINT + LIVE PING TRACKER", 18, 13, 12, Cyan, true));
        endpointTitle = new Label { Text = "—", Location = new Point(20, 48), Size = new Size(500, 34), Font = new Font("Segoe UI Semibold", 16, FontStyle.Bold), ForeColor = Text };
        result.Controls.Add(endpointTitle);
        metricLabel = new Label { Text = "LATENCY   — ms\r\nLOSS      — %\r\nJITTER    — ms\r\nSTABILITY —", Location = new Point(20, 88), Size = new Size(280, 95), Font = new Font("Cascadia Mono", 10), ForeColor = Green };
        result.Controls.Add(metricLabel);
        qualityLabel.Text = "WAITING FOR A TARGET"; qualityLabel.Location = new Point(720, 16); qualityLabel.Size = new Size(280, 30); qualityLabel.TextAlign = ContentAlignment.TopRight; qualityLabel.Font = new Font("Segoe UI Semibold", 10); qualityLabel.ForeColor = Muted; result.Controls.Add(qualityLabel);
        spark.Location = new Point(310, 64); spark.Size = new Size(690, 110); spark.BackColor = Color.FromArgb(5, 11, 22); result.Controls.Add(spark);
        center.Controls.Add(result, 0, 2);

        var conCard = new V8Card { Dock = DockStyle.Fill, Accent = Blue };
        conCard.Controls.Add(MakeText("LIVE ANALYSIS CONSOLE", 16, 10, 11, Cyan, true));
        console.Location = new Point(12, 38); console.Size = new Size(1040, 140); console.BackColor = Color.FromArgb(1, 3, 8); console.ForeColor = Text; console.ReadOnly = true; console.WordWrap = false; console.ScrollBars = RichTextBoxScrollBars.Both; console.BorderStyle = BorderStyle.FixedSingle; console.Font = new Font("Cascadia Mono", 8.7f); conCard.Controls.Add(console);
        center.Controls.Add(conCard, 0, 3);
        return center;
    }

    Control BuildRight()
    {
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Bg, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 185));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(BuildInfoCard("NETWORK TELEMETRY", Cyan, networkLabel, "Press DETECT NETWORK"), 0, 0);
        right.Controls.Add(BuildInfoCard("ROUTER INTELLIGENCE", Purple, routerLabel, "Press DETECT ROUTER"), 0, 1);
        right.Controls.Add(BuildGuideCard(), 0, 2);
        var safe = new V8Card { Dock = DockStyle.Fill, Accent = Green };
        safe.Controls.Add(MakeText("SAFE OPTIMIZATION", 14, 12, 11, Green, true));
        safe.Controls.Add(MakeText("This build recommends improvements without silently changing your PC.\r\n\r\n• test the best endpoint repeatedly\r\n• compare jitter and loss, not only ping\r\n• keep the game in an online match\r\n• use Path Quality before deciding\r\n• Save Report stores evidence for comparison", 14, 42, 8.7f, Muted));
        right.Controls.Add(safe, 0, 3);
        return right;
    }

    Control BuildInfoCard(string title, Color accent, Label value, string initial)
    {
        var card = new V8Card { Dock = DockStyle.Fill, Accent = accent };
        card.Controls.Add(MakeText(title, 14, 12, 11, accent, true));
        value.Text = initial; value.Location = new Point(14, 44); value.Size = new Size(260, 112); value.Font = new Font("Cascadia Mono", 8.6f); value.ForeColor = Text; card.Controls.Add(value);
        return card;
    }

    Control BuildGuideCard()
    {
        var card = new V8Card { Dock = DockStyle.Fill, Accent = Green };
        card.Controls.Add(MakeText("WHAT TO PRESS • IN ORDER", 14, 11, 11, Green, true));
        guideLabel.Text = "1  Launch your game and enter an online match.\r\n2  Press AUTO ANALYZE.\r\n3  Wait for Best Endpoint to fill.\r\n4  Press PING 30x to confirm stability.\r\n5  Press TRACEROUTE / PATH QUALITY.\r\n6  Press SAVE REPORT.\r\n\r\nYou normally do NOT type anything into ENDPOINT.";
        guideLabel.Location = new Point(14, 40); guideLabel.Size = new Size(270, 135); guideLabel.Font = new Font("Segoe UI", 8.3f); guideLabel.ForeColor = Text; card.Controls.Add(guideLabel);
        return card;
    }

    Control BuildFooter()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(4, 8, 18) };
        p.Controls.Add(MakeText("GAME ROUTE LAB v8.0  •  READ-ONLY  •  NO ROUTE/DNS CHANGES", 14, 7, 8.7f, Green, true));
        p.Controls.Add(MakeText("LOW-OVERHEAD ANIMATION", 480, 7, 8.7f, Cyan, true));
        p.Resize += (_, _) => { var s = MakeText("●  READY", 0, 7, 8.7f, Green, true, ContentAlignment.MiddleRight, 90, 20); s.Parent = p; s.Location = new Point(p.ClientSize.Width - 105, 7); };
        return p;
    }

    void AddTool(FlowLayoutPanel flow, string text, Color accent, Func<Task> action)
    {
        var b = new V8ToolButton { Text = text, Accent = accent, Size = new Size(105, 70), Margin = new Padding(4, 1, 4, 0) };
        b.Click += async (_, _) =>
        {
            if (b.Busy) return;
            b.Busy = true;
            try { await action(); } catch (Exception ex) { Log("ERROR: " + ex.Message); } finally { b.Busy = false; }
        };
        toolButtons.Add(b); flow.Controls.Add(b);
    }

    Button MakeButton(string text, Color accent, int x, int y, int w, int h)
    {
        var b = new V8ToolButton { Text = text, Accent = accent, Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat };
        return b;
    }

    Label MakeText(string text, int x, int y, float size, Color color, bool bold = false, ContentAlignment align = ContentAlignment.TopLeft, int w = 420, int h = 28)
    {
        return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = color, BackColor = Color.Transparent, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = align, AutoEllipsis = false };
    }

    void Animate()
    {
        phase += 0.08f;
        header.Phase = phase; radar.Phase = phase; spark.Phase = phase;
        header.Invalidate(); radar.Invalidate(); spark.Invalidate();
        foreach (var b in toolButtons) if (b is V8ToolButton v) { v.Phase = phase; v.Invalidate(); }
    }

    async Task AutoAnalyzeAsync(bool background)
    {
        if (scanning) return;
        scanning = true;
        try
        {
            SetStatus("SCANNING", Purple, 5);
            if (!background || selectedPid == 0 || !IsProcessAlive(selectedPid)) RefreshGames(false);
            await Task.Delay(80);
            if (selectedPid == 0)
            {
                Log("No supported game process detected. Launch the game and press AUTO ANALYZE again.");
                SetStatus("WAITING FOR GAME", Yellow, 10);
                return;
            }
            SetStatus("GAME FOUND", Green, 20);
            await FindConnectionsAsync();
            if (targets.Count == 0)
            {
                Log("No public-looking game endpoints were found from the selected process.");
                SetStatus("NO ENDPOINT", Yellow, 30);
                return;
            }
            await TestTargetsAsync();
            await PathQualityAsync();
            SetStatus("ANALYSIS READY", Green, 100);
        }
        finally { scanning = false; }
    }

    void RefreshGames(bool log)
    {
        var before = selectedPid;
        var found = DetectGameProcesses();
        memoryPanel.SuspendLayout(); memoryPanel.Controls.Clear();
        foreach (var g in found)
        {
            var card = new V8GameCard(g.Name, g.Pid, g.Path, selectedPid == g.Pid);
            card.Click += (_, _) => SelectGame(g);
            memoryPanel.Controls.Add(card);
        }
        if (found.Count > 0 && (selectedPid == 0 || !found.Any(x => x.Pid == selectedPid))) SelectGame(found[0]);
        if (log) Log($"Game scan: {found.Count} supported running game(s) detected.");
        if (before != selectedPid && selectedPid != 0) Log($"Selected game: {selectedGame} (PID {selectedPid}).");
        memoryPanel.ResumeLayout();
    }

    List<GameProcess> DetectGameProcesses()
    {
        var list = new List<GameProcess>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var n = p.ProcessName;
                var lower = n.ToLowerInvariant();
                if (!KnownProcessNames.Any(k => lower.Contains(k))) continue;
                string path = "Path unavailable";
                try { path = p.MainModule?.FileName ?? path; } catch { }
                list.Add(new GameProcess(n, p.Id, path));
            }
            catch { }
            finally { p.Dispose(); }
        }
        foreach (var custom in LoadCustomGamePaths())
        {
            var name = Path.GetFileNameWithoutExtension(custom);
            try
            {
                var p = Process.GetProcessesByName(name).FirstOrDefault();
                if (p != null && !list.Any(x => x.Pid == p.Id)) list.Add(new GameProcess(name, p.Id, custom));
                p?.Dispose();
            }
            catch { }
        }
        return list.OrderBy(x => x.Name).ToList();
    }

    void SelectGame(GameProcess g)
    {
        selectedGame = g.Name; selectedPid = g.Pid;
        gameTitle.Text = g.Name;
        gameDetails.Text = $"PID {g.Pid}\r\n{g.Path}\r\nRunning: YES  •  connections will be collected automatically.";
        Log($"Active game: {g.Name} | PID={g.Pid}");
    }

    async Task FindConnectionsAsync()
    {
        if (selectedPid == 0) { RefreshGames(false); if (selectedPid == 0) { Log("Find Connections: no game selected."); return; } }
        targets.Clear();
        SetStatus("FINDING CONNECTIONS", Cyan, 40);
        var lines = await Task.Run(() => RunNetstat(selectedPid));
        foreach (var c in lines)
        {
            if (IPAddress.TryParse(c.Ip, out var ip) && (ip.AddressFamily == AddressFamily.InterNetwork || ip.AddressFamily == AddressFamily.InterNetworkV6) && !IsPrivate(ip) && !IPAddress.IsLoopback(ip))
                if (!targets.Any(t => t.Ip == c.Ip && t.Port == c.Port)) targets.Add(new GameTarget(c.Ip, c.Port, c.Protocol));
        }
        targets.Sort((a, b) => StringComparer.Ordinal.Compare(a.Ip, b.Ip));
        var shown = targets.Take(8).Select(t => $"{t.Protocol,-3} {t.Ip}:{t.Port}").ToArray();
        metricLabel.Text = shown.Length == 0 ? "No public endpoints found. Try while inside an online match." : string.Join("\r\n", shown);
        Log($"Connections: {targets.Count} public endpoint candidate(s) found.");
        if (targets.Count > 0) endpointBox.Text = targets[0].Ip;
        SetStatus(targets.Count > 0 ? "ENDPOINTS FOUND" : "NO ENDPOINT", targets.Count > 0 ? Green : Yellow, 50);
    }

    List<GameTarget> RunNetstat(int pid)
    {
        var result = new List<GameTarget>();
        try
        {
            using var proc = new Process { StartInfo = new ProcessStartInfo { FileName = "netstat.exe", Arguments = "-ano -p tcp", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            proc.Start(); var text = proc.StandardOutput.ReadToEnd(); proc.WaitForExit(2500);
            foreach (var line in text.Split('\n'))
            {
                var m = Regex.Match(line.Trim(), @"^(TCP)\s+(\S+):(\d+)\s+(\S+):(\d+)\s+(\S+)\s+(\d+)$", RegexOptions.IgnoreCase);
                if (!m.Success || !int.TryParse(m.Groups[6].Value, out var linePid) || linePid != pid) continue;
                var remote = m.Groups[4].Value;
                if (remote.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) || remote.Equals("[::]", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new GameTarget(remote.Trim('[', ']'), int.Parse(m.Groups[5].Value), "TCP"));
            }
        }
        catch (Exception ex) { Log("netstat error: " + ex.Message); }
        return result;
    }

    async Task TestTargetsAsync()
    {
        SetStatus("TESTING ENDPOINTS", Blue, 65);
        var candidates = targets.Take(12).ToList();
        if (!string.IsNullOrWhiteSpace(endpointBox.Text) && IPAddress.TryParse(endpointBox.Text.Trim(), out _)) candidates.Insert(0, new GameTarget(endpointBox.Text.Trim(), 0, "MAN"));
        var results = new List<(GameTarget T, long ms)>();
        foreach (var t in candidates.DistinctBy(x => x.Ip))
        {
            var ms = await PingOnce(t.Ip, 1200);
            Log($"Ping {t.Ip}: {(ms < 0 ? "timeout" : ms + " ms")}");
            if (ms >= 0) results.Add((t, ms));
        }
        if (results.Count == 0) { endpointTitle.Text = "No pingable endpoint yet"; lastLatency = -1; return; }
        var bestResult = results.OrderBy(x => x.ms).First();
        bestIp = bestResult.T.Ip; bestPort = bestResult.T.Port; lastLatency = (int)bestResult.ms;
        endpointTitle.Text = $"{bestIp}:{bestPort}  ({bestResult.T.Protocol})";
        metricLabel.Text = $"LATENCY   {bestResult.ms} ms\r\nLOSS      0 % (single test)\r\nJITTER    measuring…\r\nSTABILITY measuring…";
        spark.AddValue((int)bestResult.ms);
        SetStatus("BEST ENDPOINT FOUND", Green, 80);
    }

    async Task<long> PingOnce(string ip, int timeout)
    {
        try { using var ping = new Ping(); var r = await ping.SendPingAsync(ip, timeout); return r.Status == IPStatus.Success ? r.RoundtripTime : -1; }
        catch { return -1; }
    }

    async Task PingBestAsync()
    {
        if (string.IsNullOrWhiteSpace(bestIp)) { await FindConnectionsAsync(); await TestTargetsAsync(); }
        if (string.IsNullOrWhiteSpace(bestIp)) return;
        SetStatus("PING 30x", Green, 85);
        var values = new List<long>(); int loss = 0;
        for (int i = 0; i < 30; i++)
        {
            var ms = await PingOnce(bestIp!, 1500); if (ms < 0) loss++; else { values.Add(ms); spark.AddValue((int)ms); lastLatency = (int)ms; }
            await Task.Delay(45);
        }
        if (values.Count == 0) { metricLabel.Text = "LATENCY   timeout\r\nLOSS      100 %\r\nJITTER    —\r\nSTABILITY offline"; qualityLabel.Text = "NO REPLIES"; return; }
        var avg = values.Average(); var jitter = MeanAbsoluteDelta(values); var lossPct = loss * 100.0 / 30;
        metricLabel.Text = $"LATENCY   {avg:0} ms\r\nLOSS      {lossPct:0.0} %\r\nJITTER    {jitter:0.0} ms\r\nSTABILITY {(lossPct == 0 && jitter < 5 ? "EXCELLENT" : lossPct < 5 ? "GOOD" : "UNSTABLE")}";
        qualityLabel.Text = lossPct == 0 && jitter < 5 ? "● EXCELLENT" : lossPct < 5 ? "● GOOD" : "● UNSTABLE";
        qualityLabel.ForeColor = lossPct < 5 ? Green : Red;
        Log($"30x result: avg={avg:0}ms loss={lossPct:0.0}% jitter={jitter:0.0}ms");
        SetStatus("ANALYSIS READY", Green, 100);
    }

    async Task PathQualityAsync()
    {
        if (string.IsNullOrWhiteSpace(bestIp)) { await FindConnectionsAsync(); await TestTargetsAsync(); }
        if (string.IsNullOrWhiteSpace(bestIp)) return;
        SetStatus("PATH QUALITY", Green, 90);
        var hopText = await Task.Run(() => RunCommand("tracert.exe", $"-d -h 12 -w 500 {bestIp}"));
        var hops = hopText.Split('\n').Count(x => Regex.IsMatch(x, @"^\s*\d+\s+"));
        Log($"Path quality: approximately {hops} responding hop(s) in trace.");
        qualityLabel.Text = hops > 0 ? $"● {hops} HOPS • LIVE" : "● TRACE LIMITED";
        qualityLabel.ForeColor = hops > 0 ? Green : Yellow;
    }

    async Task TraceAsync()
    {
        if (string.IsNullOrWhiteSpace(bestIp)) { await FindConnectionsAsync(); await TestTargetsAsync(); }
        if (string.IsNullOrWhiteSpace(bestIp)) return;
        Log("Traceroute started: " + bestIp);
        var text = await Task.Run(() => RunCommand("tracert.exe", $"-d -h 16 -w 700 {bestIp}"));
        Log(text.Trim());
    }

    void DetectNetwork()
    {
        var active = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();
        var ip = active.SelectMany(n => n.GetIPProperties().UnicastAddresses).FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "—";
        var dns = active.SelectMany(n => n.GetIPProperties().DnsAddresses).Take(3).Select(x => x.ToString()).ToArray();
        var gw = active.SelectMany(n => n.GetIPProperties().GatewayAddresses).FirstOrDefault()?.Address?.ToString() ?? "—";
        networkLabel.Text = $"INTERFACE  {active.FirstOrDefault()?.Name ?? "—"}\r\nLOCAL IP   {ip}\r\nGATEWAY    {gw}\r\nDNS        {(dns.Length == 0 ? "—" : string.Join(", ", dns))}\r\nLINK       {active.FirstOrDefault()?.NetworkInterfaceType}";
        Log($"Network detected: {active.FirstOrDefault()?.Name ?? "none"} | IP={ip} | GW={gw}");
    }

    void DetectRouter()
    {
        var gw = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().GatewayAddresses).FirstOrDefault()?.Address?.ToString() ?? "—";
        routerLabel.Text = $"GATEWAY     {gw}\r\nROUTE STATE  MONITORED\r\nMODE        READ-ONLY\r\nCHANGES     NONE\r\nCONFIDENCE  { (gw == "—" ? "LOW" : "HIGH") }";
        Log("Router detection: gateway=" + gw);
    }

    void AddGame()
    {
        using var dlg = new OpenFileDialog { Title = "Add a game executable", Filter = "Game executable (*.exe)|*.exe|All files (*.*)|*.*", CheckFileExists = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var path = dlg.FileName;
        var paths = LoadCustomGamePaths(); if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) { File.AppendAllLines(gamesFile, new[] { path }); }
        Log("Added game executable: " + path);
        RefreshGames(true);
    }

    List<string> LoadCustomGamePaths() => File.Exists(gamesFile) ? File.ReadAllLines(gamesFile).Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
    void LoadCustomGames() { _ = LoadCustomGamePaths(); }

    void ClearMemory()
    {
        try { if (File.Exists(gamesFile)) File.Delete(gamesFile); } catch { }
        memoryPanel.Controls.Clear(); targets.Clear(); selectedGame = null; selectedPid = 0; bestIp = null; endpointTitle.Text = "—"; gameTitle.Text = "No game detected"; gameDetails.Text = "Launch the game, enter an online match, then press AUTO ANALYZE."; Log("Custom game memory cleared.");
    }

    void ShowGuide()
    {
        MessageBox.Show(this, "BEST WORKFLOW\r\n\r\n1. Launch the game and enter an online match.\r\n2. Press AUTO ANALYZE.\r\n3. The app finds the game process automatically.\r\n4. It collects public TCP connections automatically.\r\n5. It pings candidates and chooses the lowest responsive endpoint.\r\n6. Press PING 30x to measure loss and jitter.\r\n7. Press TRACEROUTE and PATH QUALITY.\r\n8. Press SAVE REPORT.\r\n\r\nIf the game is not detected, press ADD GAME EXE once and select the game's .exe. You normally leave ENDPOINT empty.", "Game Route Lab v8 — Quick Guide", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void SaveReport()
    {
        using var dlg = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = $"GameRouteLab-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, $"GAME ROUTE LAB v8 REPORT\r\nGenerated: {DateTime.Now}\r\nGame: {selectedGame ?? "none"}\r\nPID: {selectedPid}\r\nBest endpoint: {bestIp}:{bestPort}\r\nLast latency: {lastLatency} ms\r\n\r\nConsole:\r\n{console.Text}");
        Log("Report saved: " + dlg.FileName);
    }

    void SetStatus(string text, Color color, int value)
    {
        statusLabel.Text = text; statusLabel.ForeColor = color; progress.Value = Math.Clamp(value, 0, 100); header.Accent = color; header.Invalidate();
    }

    void Log(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => Log(text))); return; }
        console.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}\r\n"); console.SelectionStart = console.TextLength; console.ScrollToCaret();
    }

    static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
        var b = ip.GetAddressBytes(); return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    static double MeanAbsoluteDelta(List<long> v) { if (v.Count < 2) return 0; double s = 0; for (int i = 1; i < v.Count; i++) s += Math.Abs(v[i] - v[i - 1]); return s / (v.Count - 1); }
    static bool IsProcessAlive(int pid) { try { using var p = Process.GetProcessById(pid); return !p.HasExited; } catch { return false; } }
    static string RunCommand(string file, string args)
    {
        try { using var p = new Process { StartInfo = new ProcessStartInfo { FileName = file, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } }; p.Start(); var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit(20000); return string.IsNullOrWhiteSpace(o) ? e : o; }
        catch (Exception ex) { return ex.Message; }
    }

    readonly record struct GameProcess(string Name, int Pid, string Path);
    readonly record struct GameTarget(string Ip, int Port, string Protocol);
}

sealed class V8Card : Panel
{
    public Color Accent { get; set; }
    public V8Card() { DoubleBuffered = true; BackColor = Color.FromArgb(7, 12, 25); Padding = new Padding(0); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Color.FromArgb(70, Accent), 1); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using var glow = new Pen(Color.FromArgb(150, Accent), 2); e.Graphics.DrawLine(glow, 12, 0, Math.Min(190, Width - 12), 0);
    }
}

sealed class V8ToolButton : Button
{
    public Color Accent { get; set; } = Color.Cyan; public float Phase { get; set; } public bool Busy { get; set; }
    public V8ToolButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 1; BackColor = Color.FromArgb(7, 13, 27); ForeColor = Color.FromArgb(235, 244, 255); Font = new Font("Segoe UI Semibold", 8.2f); Cursor = Cursors.Hand; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using var p = new Pen(Color.FromArgb(120, Accent), 1); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
        if (Busy) { using var b = new SolidBrush(Color.FromArgb(45, Accent)); var x = (int)((Math.Sin(Phase) + 1) * (Width - 20) / 2); e.Graphics.FillRectangle(b, x, Height - 3, 18, 3); }
    }
}

sealed class V8GameCard : Panel
{
    readonly Label name; readonly Label meta; public V8GameCard(string n, int pid, string path, bool active)
    {
        Width = 215; Height = 76; Margin = new Padding(2, 3, 2, 3); BackColor = Color.FromArgb(8, 16, 30); Cursor = Cursors.Hand; DoubleBuffered = true;
        name = new Label { Text = n, Location = new Point(14, 10), Size = new Size(185, 24), ForeColor = active ? Color.FromArgb(37, 242, 116) : Color.FromArgb(235, 244, 255), Font = new Font("Segoe UI Semibold", 10), AutoEllipsis = true };
        meta = new Label { Text = $"PID {pid}\r\n{Path.GetFileName(path)}", Location = new Point(14, 36), Size = new Size(185, 32), ForeColor = Color.FromArgb(133, 158, 190), Font = new Font("Cascadia Mono", 7.5f), AutoEllipsis = true };
        Controls.Add(name); Controls.Add(meta);
    }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p = new Pen(Color.FromArgb(80, 0, 231, 255)); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
}

sealed class V8Header : Panel
{
    public string Title { get; set; } = "GAME ROUTE LAB"; public string Subtitle { get; set; } = "SMARTER ROUTES. BETTER PING."; public string Tagline { get; set; } = "LOCAL-FIRST GAME NETWORK ANALYZER"; public Point StatusLocation { get; set; } public float Phase { get; set; } public Color Accent { get; set; } = Color.FromArgb(37, 242, 116);
    public V8Header() { BackColor = Color.FromArgb(3, 6, 15); DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var line = new Pen(Color.FromArgb(180, 185, 74, 255), 3); e.Graphics.DrawLine(line, 4, Height - 3, Width / 2, Height - 3); using var cyan = new Pen(Color.FromArgb(220, 0, 231, 255), 3); e.Graphics.DrawLine(cyan, Width / 2, Height - 3, Width - 4, Height - 3);
        using var logoPen = new Pen(Color.FromArgb(210, 185, 74, 255), 3); using var cyanPen = new Pen(Color.FromArgb(210, 0, 231, 255), 2); var c = new Point(82, 61);
        e.Graphics.DrawPolygon(logoPen, new[] { new Point(82, 10), new Point(122, 31), new Point(113, 93), new Point(82, 116), new Point(51, 93), new Point(42, 31) }); e.Graphics.DrawEllipse(cyanPen, 54, 27, 56, 64); e.Graphics.DrawString("GRL", new Font("Segoe UI Semibold", 24, FontStyle.Bold), Brushes.White, 54, 47);
        using var b = new SolidBrush(TextColor); e.Graphics.DrawString(Title, new Font("Segoe UI Semibold", 29, FontStyle.Bold), b, 150, 27); using var s = new SolidBrush(Color.FromArgb(0, 231, 255)); e.Graphics.DrawString(Subtitle, new Font("Segoe UI Semibold", 12, FontStyle.Bold), s, 153, 67); using var m = new SolidBrush(Color.FromArgb(133, 158, 190)); e.Graphics.DrawString(Tagline, new Font("Segoe UI", 8.5f), m, 154, 91);
        var rect = new Rectangle(Math.Max(720, Width - 270), 24, 244, 66); using var rp = new Pen(Color.FromArgb(130, Accent), 1); e.Graphics.DrawRectangle(rp, rect); using var dot = new SolidBrush(Accent); e.Graphics.FillEllipse(dot, rect.X + 16, rect.Y + 25, 7, 7); using var st = new SolidBrush(TextColor); e.Graphics.DrawString("SYSTEM STATUS", new Font("Segoe UI", 7), m, rect.X + 14, rect.Y + 9); e.Graphics.DrawString(Accent == Color.FromArgb(37, 242, 116) ? "READY • READ-ONLY" : "ACTIVE • READ-ONLY", new Font("Segoe UI Semibold", 8.5f), st, rect.X + 28, rect.Y + 22);
    }
}

sealed class V8Radar : Control
{
    public float Phase { get; set; } public V8Radar() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; var c = new Point(Width / 2, Height / 2); int r = Math.Min(Width, Height) / 2 - 10;
        for (int i = 1; i <= 4; i++) using (var p = new Pen(Color.FromArgb(100, 185, 74, 255), 1)) e.Graphics.DrawEllipse(p, c.X - r * i / 4, c.Y - r * i / 4, r * i / 2, r * i / 2);
        using var line = new Pen(Color.FromArgb(150, 0, 231, 255), 1); e.Graphics.DrawLine(line, c.X, c.Y - r, c.X, c.Y + r); e.Graphics.DrawLine(line, c.X - r, c.Y, c.X + r, c.Y);
        var a = Phase; var end = new Point((int)(c.X + Math.Cos(a) * r), (int)(c.Y + Math.Sin(a) * r)); using var sweep = new Pen(Color.FromArgb(210, 185, 74, 255), 2); e.Graphics.DrawLine(sweep, c, end); using var dot = new SolidBrush(Color.FromArgb(0, 231, 255)); e.Graphics.FillEllipse(dot, c.X - 5, c.Y - 5, 10, 10);
    }
}

sealed class V8Sparkline : Control
{
    readonly Queue<int> values = new(); public float Phase { get; set; }
    public V8Sparkline() { DoubleBuffered = true; for (int i = 0; i < 24; i++) values.Enqueue(0); }
    public void AddValue(int value) { values.Enqueue(Math.Clamp(value, 0, 1000)); while (values.Count > 32) values.Dequeue(); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; for (int y = 20; y < Height; y += 25) using (var p = new Pen(Color.FromArgb(30, 130, 160, 190))) e.Graphics.DrawLine(p, 0, y, Width, y);
        var a = values.ToArray(); if (a.Length < 2) return; int max = Math.Max(100, a.Max()); var pts = new Point[a.Length]; for (int i = 0; i < a.Length; i++) pts[i] = new Point(i * Math.Max(1, Width - 12) / (a.Length - 1) + 6, Height - 8 - (int)((Height - 20) * a[i] / (double)max)); using var pen = new Pen(Color.FromArgb(230, 37, 242, 116), 2); e.Graphics.DrawLines(pen, pts); using var dot = new SolidBrush(Color.FromArgb(37, 242, 116)); var q = pts[^1]; e.Graphics.FillEllipse(dot, q.X - 3, q.Y - 3, 6, 6);
    }
}

static class V8Colors { public static readonly Color TextColor = Color.FromArgb(239, 246, 255); }
