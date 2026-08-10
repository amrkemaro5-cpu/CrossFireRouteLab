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
    static readonly Color Line = Color.FromArgb(27, 54, 88);
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
    readonly Label gameName = new(), gameMeta = new(), network = new(), router = new(), best = new(), metrics = new(), quality = new(), tips = new(), progressText = new(), systemText = new(), connections = new(), analysisTitle = new();
    readonly TextBox endpoint = new();
    readonly GlowProgress progress = new();
    readonly RadarControl radar = new();
    readonly SparklineControl graph = new();
    readonly List<IconButton> actions = new();
    readonly List<GameProfile> memory = new();
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab");
    GameProfile? current;
    bool busy;

    public ModernMainForm()
    {
        Text = "Game Route Lab";
        Width = 1600;
        Height = 980;
        MinimumSize = new Size(1180, 760);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.5f);
        DoubleBuffered = true;
        Directory.CreateDirectory(dataDir);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        memory.AddRange(GameProfileStore.Load());
        Build();
        RefreshMemory();
        Log("GAME ROUTE LAB v6.0");
        Log("Smart game detection • ISP • router • endpoint • route quality • local game memory");
        Log("READ-ONLY MODE: no routes, DNS, PPPoE, router settings or firmware are changed.");
    }

    void Build()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 1, RowCount = 4, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 136));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 3, RowCount = 1, Padding = new Padding(12, 8, 12, 8) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 275));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
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
        p.Controls.Add(new PictureBox { Image = Brand.CreateLogo(108), Size = new Size(112, 112), Location = new Point(28, 10), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent });
        p.Controls.Add(LabelOf("GAME ROUTE LAB", new Point(158, 23), new Size(650, 42), 31, TextColor, true));
        p.Controls.Add(LabelOf("SMARTER ROUTES.  BETTER PING.", new Point(162, 70), new Size(650, 24), 12, Cyan, true));
        p.Controls.Add(LabelOf("LOCAL-FIRST GAME NETWORK ANALYZER", new Point(163, 99), new Size(650, 20), 9, Muted));
        var badge = new StatusBadge { Size = new Size(250, 76), Location = new Point(1280, 28), Accent = Green, Title = "SYSTEM STATUS", Value = "READY • READ-ONLY" };
        p.Controls.Add(badge);
        p.Resize += (_, _) => badge.Location = new Point(Math.Max(700, p.ClientSize.Width - badge.Width - 28), 28);
        return p;
    }

    Control BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(3, 7, 16) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 8), WrapContents = false, AutoScroll = true, BackColor = Color.Transparent };
        flow.Controls.Add(LabelOf("ENDPOINT", Point.Empty, new Size(62, 76), 8, Muted, true));
        endpoint.Width = 180; endpoint.Height = 40; endpoint.Margin = new Padding(2, 8, 6, 0); endpoint.BackColor = Surface2; endpoint.ForeColor = TextColor; endpoint.BorderStyle = BorderStyle.FixedSingle; endpoint.PlaceholderText = "Optional IP / hostname"; flow.Controls.Add(endpoint);
        AddAction(flow, "◎", "AUTO ANALYZE", AutoAnalyze, Purple); AddAction(flow, "⌁", "REFRESH GAMES", RefreshGames, Cyan); AddAction(flow, "◌", "DETECT NETWORK", DetectNetwork, Cyan); AddAction(flow, "▣", "DETECT ROUTER", DetectRouter, Purple); AddAction(flow, "⌕", "FIND CONNECTIONS", FindConnections, Cyan); AddAction(flow, "⌁", "ROUTE TABLE", RouteTable, Blue); AddAction(flow, "◉", "PING 30x", Ping30, Green); AddAction(flow, "⇢", "TRACEROUTE", Traceroute, Purple); AddAction(flow, "▥", "PATH QUALITY", PathQuality, Green); AddAction(flow, "▤", "SAVE REPORT", SaveReport, Purple);
        bar.Controls.Add(flow); return bar;
    }

    void AddAction(Control parent, string glyph, string title, Func<Task> action, Color accent)
    {
        var b = new IconButton(glyph, title, accent) { Width = 108, Height = 78, Margin = new Padding(3, 1, 3, 1) }; b.Click += async (_, _) => await Safe(action); actions.Add(b); parent.Controls.Add(b);
    }

    Control BuildLeft()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, RowCount = 2, ColumnCount = 1, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));
        var memoryCard = new NeonCard { Dock = DockStyle.Fill, Accent = Purple };
        memoryCard.Controls.Add(LabelOf("GAME MEMORY", new Point(18, 14), new Size(220, 28), 13, Purple, true)); memoryCard.Controls.Add(LabelOf("YOUR LOCAL HISTORY", new Point(18, 41), new Size(220, 18), 8, Muted, true));
        games.Location = new Point(10, 68); games.Size = new Size(255, 430); games.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; games.FlowDirection = FlowDirection.TopDown; games.WrapContents = false; games.AutoScroll = true; games.BackColor = Color.Transparent; memoryCard.Controls.Add(games);
        var all = new NeonButton("⌁  VIEW ALL GAMES", Purple) { Size = new Size(235, 40), Anchor = AnchorStyles.Bottom | AnchorStyles.Left, Location = new Point(18, 0) }; all.Click += (_, _) => AllGames(); memoryCard.Controls.Add(all); memoryCard.Resize += (_, _) => { all.Location = new Point(18, memoryCard.ClientSize.Height - all.Height - 16); games.Size = new Size(Math.Max(100, memoryCard.ClientSize.Width - 20), Math.Max(100, memoryCard.ClientSize.Height - 126)); };
        root.Controls.Add(memoryCard, 0, 0);
        var quick = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan }; quick.Controls.Add(LabelOf("QUICK ACTIONS", new Point(18, 12), new Size(220, 25), 12, Cyan, true));
        AddQuick(quick, "♜  CLEAR MEMORY", Purple, 47, () => { memory.Clear(); GameProfileStoreClear(); RefreshMemory(); }); AddQuick(quick, "⇩  EXPORT ALL REPORTS", Cyan, 86, () => SaveReport().GetAwaiter().GetResult()); AddQuick(quick, "⚙  SETTINGS", Purple, 125, () => MessageBox.Show("Game Route Lab runs in READ-ONLY mode. Network analysis does not change router, DNS, PPPoE or Windows route settings.", "Game Route Lab", MessageBoxButtons.OK, MessageBoxIcon.Information));
        root.Controls.Add(quick, 0, 1); return root;
    }

    void AddQuick(Control parent, string text, Color accent, int y, Action action) { var b = new NeonButton(text, accent) { Location = new Point(18, y), Size = new Size(235, 34) }; b.Click += (_, _) => action(); parent.Controls.Add(b); }

    Control BuildCenter()
    {
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, RowCount = 4, ColumnCount = 1, Padding = new Padding(0) };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 27)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 29)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 24)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 20));
        center.Controls.Add(BuildHero(), 0, 0); center.Controls.Add(BuildSummary(), 0, 1); center.Controls.Add(BuildBest(), 0, 2); center.Controls.Add(BuildConsole(), 0, 3); return center;
    }

    Control BuildHero()
    {
        var p = new NeonCard { Dock = DockStyle.Fill, Accent = Purple }; p.Controls.Add(radar); radar.Size = new Size(136, 136); radar.Location = new Point(18, 24);
        analysisTitle.Text = "AUTO ANALYSIS IN PROGRESS"; analysisTitle.Location = new Point(174, 20); analysisTitle.Size = new Size(680, 34); analysisTitle.Font = new Font("Segoe UI Semibold", 20, FontStyle.Bold); analysisTitle.ForeColor = Purple; p.Controls.Add(analysisTitle);
        p.Controls.Add(LabelOf("Detecting the game, connections and route quality automatically...", new Point(174, 57), new Size(720, 24), 10, Muted)); progress.Location = new Point(174, 91); progress.Size = new Size(720, 12); p.Controls.Add(progress); progressText.Text = "READY"; progressText.Location = new Point(906, 84); progressText.Size = new Size(65, 26); progressText.ForeColor = TextColor; progressText.Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold); p.Controls.Add(progressText);
        var stages = new[] { ("✓", "DETECT GAME", Green), ("✓", "FIND CONNECTIONS", Green), ("✓", "TEST ENDPOINTS", Green), ("4", "ANALYZE ROUTES", Purple), ("5", "GENERATE REPORT", Muted) };
        for (var i = 0; i < stages.Length; i++) { var x = 155 + i * 177; p.Controls.Add(LabelOf(stages[i].Item1, new Point(x, 119), new Size(165, 25), 13, stages[i].Item3, true, ContentAlignment.MiddleCenter)); p.Controls.Add(LabelOf(stages[i].Item2, new Point(x, 144), new Size(165, 20), 8, stages[i].Item3, true, ContentAlignment.MiddleCenter)); }
        return p;
    }

    Control BuildSummary()
    {
        var p = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan }; p.Controls.Add(LabelOf("CURRENT ANALYSIS SUMMARY", new Point(18, 13), new Size(500, 27), 13, Cyan, true));
        var active = new NeonInnerCard { Location = new Point(16, 48), Size = new Size(410, 170), Accent = Cyan }; active.Controls.Add(LabelOf("⌁  ACTIVE GAME", new Point(18, 13), new Size(270, 24), 11, Cyan, true)); gameName.Text = "No game detected"; gameName.Location = new Point(18, 47); gameName.Size = new Size(360, 30); gameName.Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold); active.Controls.Add(gameName); gameMeta.Text = "Start an online game and click AUTO ANALYZE"; gameMeta.Location = new Point(18, 82); gameMeta.Size = new Size(370, 68); gameMeta.ForeColor = Muted; active.Controls.Add(gameMeta); p.Controls.Add(active);
        var conn = new NeonInnerCard { Location = new Point(438, 48), Size = new Size(440, 170), Accent = Cyan }; conn.Controls.Add(LabelOf("⚙  CONNECTIONS DISCOVERED", new Point(18, 13), new Size(390, 24), 11, Cyan, true)); connections.Text = "No endpoints discovered yet.\n\nAUTO ANALYZE will find public connections for the active game automatically."; connections.Location = new Point(18, 49); connections.Size = new Size(400, 102); connections.ForeColor = Muted; conn.Controls.Add(connections); p.Controls.Add(conn); return p;
    }

    Control BuildBest()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 2, RowCount = 1, Padding = new Padding(0) }; row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        var bestCard = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan }; bestCard.Controls.Add(LabelOf("🏆  BEST ENDPOINT (CURRENT)", new Point(18, 13), new Size(420, 28), 12, Cyan, true)); best.Text = "—"; best.Location = new Point(18, 47); best.Size = new Size(500, 30); best.Font = new Font("Segoe UI Semibold", 16, FontStyle.Bold); bestCard.Controls.Add(best); metrics.Text = "LATENCY     — ms\nLOSS        —\nJITTER      — ms\nSTABILITY   —"; metrics.Location = new Point(18, 84); metrics.Size = new Size(300, 78); metrics.Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold); metrics.ForeColor = Green; bestCard.Controls.Add(metrics); row.Controls.Add(bestCard, 0, 0);
        var qualityCard = new NeonCard { Dock = DockStyle.Fill, Accent = Green }; qualityCard.Controls.Add(LabelOf("ROUTE QUALITY", new Point(18, 13), new Size(220, 27), 12, Cyan, true)); quality.Text = "WAITING"; quality.Location = new Point(300, 12); quality.Size = new Size(110, 27); quality.TextAlign = ContentAlignment.MiddleRight; quality.Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold); quality.ForeColor = Muted; qualityCard.Controls.Add(quality); graph.Location = new Point(18, 49); graph.Size = new Size(390, 82); qualityCard.Controls.Add(graph); qualityCard.Controls.Add(LabelOf("Hops: —   |   Avg Latency: —", new Point(18, 137), new Size(390, 22), 9, Muted)); row.Controls.Add(qualityCard, 1, 0); return row;
    }

    Control BuildConsole()
    {
        var p = new NeonCard { Dock = DockStyle.Fill, Accent = Blue }; p.Controls.Add(LabelOf("LIVE ANALYSIS CONSOLE", new Point(18, 9), new Size(350, 24), 11, Cyan, true)); console.Location = new Point(12, 39); console.Size = new Size(900, 100); console.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom; console.BackColor = Color.FromArgb(1, 4, 10); console.ForeColor = TextColor; console.BorderStyle = BorderStyle.None; console.Font = new Font("Cascadia Mono", 8.8f); console.ReadOnly = true; console.WordWrap = false; p.Controls.Add(console); return p;
    }

    Control BuildRight()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, RowCount = 3, ColumnCount = 1, Padding = Padding.Empty }; root.RowStyles.Add(new RowStyle(SizeType.Percent, 30)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 34)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        var n = new NeonCard { Dock = DockStyle.Fill, Accent = Cyan }; n.Controls.Add(LabelOf("◌  NETWORK INFORMATION", new Point(18, 15), new Size(300, 28), 12, Cyan, true)); network.Text = "ISP\t—\nASN\t—\nPublic IP\t—\nLocation\t—\nConnection\t—\nDNS\t—"; network.Location = new Point(20, 54); network.Size = new Size(300, 150); network.Font = new Font("Segoe UI", 9.5f); n.Controls.Add(network); root.Controls.Add(n, 0, 0);
        var r = new NeonCard { Dock = DockStyle.Fill, Accent = Purple }; r.Controls.Add(LabelOf("▣  ROUTER INTELLIGENCE", new Point(18, 15), new Size(300, 28), 12, Purple, true)); router.Text = "Gateway\t—\nManufacturer\t—\nModel\t—\nFirmware\t—\nInterface\t—\nConfidence\t—"; router.Location = new Point(20, 54); router.Size = new Size(300, 190); router.Font = new Font("Segoe UI", 9.5f); r.Controls.Add(router); root.Controls.Add(r, 0, 1);
        var z = new NeonCard { Dock = DockStyle.Fill, Accent = Purple }; z.Controls.Add(LabelOf("◉  TIPS", new Point(18, 15), new Size(300, 28), 12, Purple, true)); tips.Text = "Run analysis while the game is in an online match.\n\nMore observations = better local memory.\n\nICMP-blocked servers are not automatically treated as packet loss.\n\nThe analyzer never changes your router or Windows routes."; tips.Location = new Point(20, 55); tips.Size = new Size(300, 220); tips.ForeColor = Muted; tips.Font = new Font("Segoe UI", 9.5f); z.Controls.Add(tips); root.Controls.Add(z, 0, 2); return root;
    }

    Control BuildFooter()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(2, 6, 15) }; p.Controls.Add(LabelOf("◷  Game Route Lab v6.0   •   READ-ONLY MODE", new Point(18, 10), new Size(310, 22), 9.5f, Green, true)); systemText.Text = "▣  System: Windows 64-bit"; systemText.AutoSize = true; systemText.Location = new Point(345, 10); systemText.ForeColor = Cyan; p.Controls.Add(systemText); var ready = LabelOf("●  READY", new Point(0, 10), new Size(90, 22), 9.5f, Green, true, ContentAlignment.MiddleRight); p.Controls.Add(ready); p.Resize += (_, _) => ready.Location = new Point(p.ClientSize.Width - ready.Width - 18, 10); return p;
    }

    Label LabelOf(string text, Point location, Size size, float font, Color color, bool bold = false, ContentAlignment align = ContentAlignment.TopLeft) => new() { Text = text, Location = location, Size = size, Font = new Font("Segoe UI", font, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent, TextAlign = align };

    async Task Safe(Func<Task> action)
    {
        if (busy) return; busy = true; foreach (var a in actions) a.Enabled = false; SetProgress(5, "5%");
        try { await action(); } catch (Exception ex) { Log("[ERROR] " + ex.Message); quality.Text = "ERROR"; quality.ForeColor = Red; }
        finally { busy = false; foreach (var a in actions) a.Enabled = true; if (progress.Value < 100) SetProgress(100, "READY"); }
    }

    void SetProgress(int value, string text) { progress.Value = Math.Clamp(value, 0, 100); progressText.Text = text; radar.Invalidate(); }

    async Task AutoAnalyze()
    {
        analysisTitle.Text = "AUTO ANALYSIS IN PROGRESS"; quality.Text = "ANALYZING"; quality.ForeColor = Purple; SetProgress(8, "8%");
        Log("\n============================================================\nAUTO ANALYSIS STARTED\n============================================================");
        await DetectNetwork(); SetProgress(22, "22%"); await DetectRouter(); SetProgress(38, "38%");
        var gamesFound = await DiscoverGames();
        if (gamesFound.Count == 0) { gameName.Text = "No high-confidence game detected"; gameMeta.Text = "Start an online game and run AUTO ANALYZE again."; connections.Text = "No public game endpoints found yet."; Log("STOP: no high-confidence game was detected. Normal applications are not guessed as games."); quality.Text = "WAITING"; quality.ForeColor = Muted; return; }
        var game = gamesFound[0]; current = game; gameName.Text = current.DisplayName; gameMeta.Text = $"{current.Observations} saved analyses\nPath: {current.ExecutablePath}\nLast best: {(string.IsNullOrWhiteSpace(current.LastBestEndpoint) ? "—" : current.LastBestEndpoint)}"; SetProgress(48, "48%");
        Log($"GAME: {current.DisplayName} | executable identified | saved memory loaded");
        var live = (await GameScanner.DiscoverAsync()).FirstOrDefault(x => x.ExecutablePath.Equals(current.ExecutablePath, StringComparison.OrdinalIgnoreCase) || x.ProcessName.Equals(current.ProcessName, StringComparison.OrdinalIgnoreCase));
        var endpoints = live == null ? new List<GameEndpoint>() : await GetEndpoints(live.Pid);
        connections.Text = endpoints.Count == 0 ? "Game detected, but no public established sockets are visible.\n\nEnter an online match and retry." : string.Join("\n", endpoints.Take(5).Select(x => $"{x.Protocol}  {x.RemoteIp}:{x.RemotePort}   {x.State}")) + (endpoints.Count > 5 ? $"\n… and {endpoints.Count - 5} more" : "");
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
        metrics.Text = $"LATENCY     {(result.Probe.Avg > 0 ? result.Probe.Avg.ToString("0") : "—")} ms\nLOSS        {(result.Probe.HasResponse ? result.Probe.Loss.ToString("0") : "unknown")}\nJITTER      {(result.Probe.HasResponse ? result.Probe.Jitter.ToString("0") : "—")} ms\nSTABILITY   {result.Stability}";
        quality.Text = result.Stability.ToUpperInvariant(); quality.ForeColor = result.Stability == "Excellent" ? Green : result.Stability == "Good" ? Yellow : result.Stability == "Unknown" ? Muted : Red; graph.Values = result.Probe.History.Count > 0 ? result.Probe.History : new List<double> { 1 }; graph.Invalidate();
    }

    async Task RefreshGames() { var found = await DiscoverGames(); Log($"\n=== GAME DISCOVERY: {found.Count} candidate(s) ==="); foreach (var g in found) Log($"{g.DisplayName} | {g.ProcessName} | {g.Observations} saved observations | {g.ExecutablePath}"); }

    async Task<List<GameProfile>> DiscoverGames()
    {
        var items = await GameScanner.DiscoverAsync(); var candidates = items.Where(x => x.LikelyGame).GroupBy(x => new { x.Pid, x.ProcessName, x.ExecutablePath }).Select(g => g.OrderByDescending(x => x.Confidence).First()).OrderByDescending(x => x.Confidence).Take(12).ToList(); var profiles = new List<GameProfile>();
        foreach (var g in candidates) profiles.Add(GameProfileStore.Touch(g.ProcessName, g.ExecutablePath)); memory.Clear(); memory.AddRange(GameProfileStore.Load()); RefreshMemory(); return profiles;
    }

    async Task DetectNetwork()
    {
        var n = await NetworkProfileDetector.DetectAsync(); network.Text = $"ISP\t{n.ISP}\nASN\t{n.ASN}\nPublic IP\t{n.PublicIp}\nLocation\t{n.City}, {n.Country}\nConnection\t{n.WanType}\nDNS\t{n.DnsServers}"; systemText.Text = $"▣  System: Windows {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}   •   {n.InterfaceName}"; Log($"NETWORK: ISP={n.ISP} | Org={n.Organization} | ASN={n.ASN} | Public={n.PublicIp} | GW={n.Gateway}");
    }

    async Task DetectRouter() { var r = await RouterDetector.DetectAsync(); router.Text = $"Gateway\t{r.Gateway}\nManufacturer\t{r.Vendor}\nModel\t{r.Model}\nFirmware\t{r.Firmware}\nInterface\t{r.ManagementUrl}\nConfidence\t{r.Confidence}"; Log($"ROUTER: {r.Vendor} {r.Model} | firmware {r.Firmware} | confidence {r.Confidence}"); }

    async Task FindConnections()
    {
        var found = await GameScanner.DiscoverAsync(); var game = found.FirstOrDefault(x => x.LikelyGame); if (game == null) { Log("No high-confidence game connection found."); return; } var eps = await GetEndpoints(game.Pid); connections.Text = eps.Count == 0 ? "No public established sockets visible." : string.Join("\n", eps.Select(x => $"{x.Protocol}  {x.RemoteIp}:{x.RemotePort}   {x.State}")); foreach (var ep in eps) Log($"{ep.Protocol} {ep.RemoteIp}:{ep.RemotePort} {ep.State}");
    }

    async Task RouteTable() => Log("\n=== ROUTE TABLE ===\n" + await Run("route.exe", "print", 10000));

    async Task Ping30() { var ip = Target(); if (ip.Length == 0) { Log("No endpoint selected."); return; } Log($"\n=== PING 30x {ip} ===\n" + await Run("ping.exe", $"-n 30 {ip}", 50000)); }
    async Task Traceroute() { var ip = Target(); if (ip.Length == 0) { Log("No endpoint selected."); return; } Log($"\n=== TRACEROUTE {ip} ===\n" + await Run("tracert.exe", $"-d -h 30 -w 700 {ip}", 45000)); }

    async Task PathQuality()
    {
        var ip = Target(); if (ip.Length == 0) { Log("No endpoint selected."); return; } var p = await Probe(ip); var t = await Trace(ip); var ep = new GameEndpoint("manual", 0, "TCP", ip, 0, "MANUAL", false, 0, ""); ApplyResult(new RouteResult(ep, p, t)); Log($"PATH QUALITY: {(p.HasResponse ? p.Avg.ToString("0") : "blocked/unknown")} ms | loss {(p.HasResponse ? p.Loss.ToString("0") : "unknown")} | jitter {(p.HasResponse ? p.Jitter.ToString("0") : "unknown")} | hops {t.Hops}");
    }

    async Task SaveReport()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"GameRouteLab_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt"); var text = console.Text + Environment.NewLine + "=== CURRENT RESULT ===" + Environment.NewLine + best.Text + Environment.NewLine + metrics.Text; await File.WriteAllTextAsync(file, text); Log("Report saved: " + file);
    }

    string Target() { if (!string.IsNullOrWhiteSpace(endpoint.Text)) return endpoint.Text.Trim(); if (string.IsNullOrWhiteSpace(current?.LastBestEndpoint)) return ""; var s = current.LastBestEndpoint; var k = s.LastIndexOf(':'); return k > 0 ? s[..k] : s; }

    async Task<List<GameEndpoint>> GetEndpoints(int pid) { var all = await GameScanner.DiscoverAsync(); return all.Where(x => x.Pid == pid && x.LikelyGame).GroupBy(x => $"{x.Protocol}|{x.RemoteIp}|{x.RemotePort}").Select(g => g.First()).ToList(); }

    async Task<ProbeResult> Probe(string host)
    {
        var samples = new List<long>(); for (var i = 0; i < 5; i++) { try { using var p = new Ping(); var r = await p.SendPingAsync(host, 900); if (r.Status == IPStatus.Success) samples.Add(r.RoundtripTime); } catch { } }
        if (samples.Count == 0) return new ProbeResult(0, 0, 0, new List<double>(), false); var avg = samples.Average(); var jitter = samples.Count < 2 ? 0 : samples.Zip(samples.Skip(1), (a, b) => Math.Abs(a - b)).Average(); return new ProbeResult(avg, (5 - samples.Count) * 20, jitter, samples.Select(x => (double)x).ToList(), true);
    }

    async Task<TraceResult> Trace(string host)
    {
        var text = await Run("tracert.exe", $"-d -h 18 -w 500 {host}", 24000); var hops = 0; var last = 0.0; foreach (var line in text.Split('\n')) { if (!Regex.IsMatch(line.TrimStart(), @"^\d+\s+")) continue; hops++; var ms = Regex.Matches(line, @"(\d+)\s*ms"); if (ms.Count > 0 && double.TryParse(ms[^1].Groups[1].Value, out var value)) last = value; } return new TraceResult(hops, last);
    }

    async Task<string> Run(string file, string args, int timeout)
    {
        try { using var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } }; p.Start(); var outputTask = p.StandardOutput.ReadToEndAsync(); var errorTask = p.StandardError.ReadToEndAsync(); if (!p.WaitForExit(timeout)) { try { p.Kill(true); } catch { } return await outputTask + Environment.NewLine + await errorTask + Environment.NewLine + "[Timed out]"; } return await outputTask + Environment.NewLine + await errorTask; } catch (Exception ex) { return "[Command error] " + ex.Message; }
    }

    void RefreshMemory()
    {
        games.SuspendLayout(); games.Controls.Clear(); foreach (var g in memory.OrderByDescending(x => x.LastSeenUtc).Take(8)) { var item = new GameMemoryItem(g, Green, Cyan) { Width = 247, Height = 76, Margin = new Padding(2, 2, 2, 6) }; item.Click += (_, _) => SelectGame(g); games.Controls.Add(item); } if (memory.Count == 0) games.Controls.Add(LabelOf("No games remembered yet.\n\nStart a game and click\nAUTO ANALYZE.", new Point(8, 8), new Size(230, 100), 10, Muted)); games.ResumeLayout();
    }

    void SelectGame(GameProfile profile) { current = profile; gameName.Text = profile.DisplayName; gameMeta.Text = $"{profile.Observations} saved analyses\nPath: {profile.ExecutablePath}\nBest: {(string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint)}"; best.Text = string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint; Log($"\n[MEMORY] {profile.DisplayName}\nObservations: {profile.Observations}\nLast best: {profile.LastBestEndpoint}"); }

    void AllGames()
    {
        using var f = new Form { Text = "Game Route Lab • All Games", Size = new Size(760, 560), BackColor = Bg, StartPosition = FormStartPosition.CenterParent, ForeColor = TextColor }; var list = new ListBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = TextColor, Font = new Font("Segoe UI", 11), BorderStyle = BorderStyle.None }; foreach (var g in memory) list.Items.Add($"{g.DisplayName}    •    {g.Observations} analyses    •    Best {g.LastBestEndpoint}"); f.Controls.Add(list); f.ShowDialog(this);
    }

    void GameProfileStoreClear() { try { var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab"); var file = Path.Combine(root, "profiles.json"); if (File.Exists(file)) File.Delete(file); } catch { } }
    void Log(string text) { if (console.InvokeRequired) { console.BeginInvoke(() => Log(text)); return; } console.AppendText(text + Environment.NewLine); console.SelectionStart = console.TextLength; console.ScrollToCaret(); }

    record ProbeResult(double Avg, double Loss, double Jitter, List<double> History, bool HasResponse);
    record TraceResult(int Hops, double Last);
    record RouteResult(GameEndpoint Endpoint, ProbeResult Probe, TraceResult Trace)
    {
        public double Score => Probe.HasResponse ? Math.Min(200, Probe.Avg + Probe.Loss * 2 + Trace.Hops * 0.5 + Probe.Jitter * 0.35) : 80 + Trace.Hops * 0.5;
        public string Stability => !Probe.HasResponse ? "Unknown" : Probe.Loss == 0 && Probe.Avg < 80 && Probe.Jitter < 12 ? "Excellent" : Probe.Loss < 20 && Probe.Avg < 150 ? "Good" : "Variable";
    }
}

sealed class NeonHeader : Panel
{
    protected override void OnPaintBackground(PaintEventArgs e) { using var b = new LinearGradientBrush(ClientRectangle, Color.FromArgb(1, 3, 9), Color.FromArgb(4, 8, 20), LinearGradientMode.Horizontal); e.Graphics.FillRectangle(b, ClientRectangle); using var line = new LinearGradientBrush(new Rectangle(0, Height - 4, Width, 4), Color.FromArgb(181, 70, 255), Color.FromArgb(0, 224, 255), LinearGradientMode.Horizontal); e.Graphics.FillRectangle(line, 0, Height - 4, Width, 4); }
}

sealed class NeonCard : Panel
{
    public Color Accent { get; set; } = Color.FromArgb(0, 224, 255); public NeonCard() { DoubleBuffered = true; BackColor = Color.FromArgb(6, 12, 25); Padding = new Padding(8); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; var r = new Rectangle(1, 1, Width - 3, Height - 3); using var p = new Pen(Color.FromArgb(95, Accent.R, Accent.G, Accent.B), 1); e.Graphics.DrawRectangle(p, r); using var glow = new Pen(Color.FromArgb(28, Accent.R, Accent.G, Accent.B), 3); e.Graphics.DrawLine(glow, 10, 2, Math.Min(180, Width - 10), 2); }
}

sealed class NeonInnerCard : Panel
{
    public Color Accent { get; set; } = Color.FromArgb(0, 224, 255); public NeonInnerCard() { BackColor = Color.FromArgb(9, 18, 35); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p = new Pen(Color.FromArgb(75, Accent.R, Accent.G, Accent.B)); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
}

sealed class NeonButton : Button
{
    readonly Color accent; public NeonButton(string text, Color accent) { Text = text; this.accent = accent; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 1; FlatAppearance.BorderColor = Color.FromArgb(120, accent.R, accent.G, accent.B); FlatAppearance.MouseOverBackColor = Color.FromArgb(22, 25, 50); BackColor = Color.FromArgb(7, 14, 28); ForeColor = Color.White; Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold); Cursor = Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p = new Pen(Color.FromArgb(100, accent.R, accent.G, accent.B)); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
}

sealed class IconButton : Panel
{
    readonly Color accent; readonly string glyph, title; bool hover;
    public IconButton(string glyph, string title, Color accent) { this.glyph = glyph; this.title = title; this.accent = accent; BackColor = Color.FromArgb(5, 11, 23); Cursor = Cursors.Hand; DoubleBuffered = true; MouseEnter += (_, _) => { hover = true; Invalidate(); }; MouseLeave += (_, _) => { hover = false; Invalidate(); }; }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var bg = new SolidBrush(hover ? Color.FromArgb(20, 18, 48) : Color.FromArgb(5, 11, 23)); e.Graphics.FillRectangle(bg, 0, 0, Width, Height); using var p = new Pen(hover ? accent : Color.FromArgb(35, 60, 95), hover ? 1.5f : 1); e.Graphics.DrawRectangle(p, 1, 1, Width - 3, Height - 3); using var f = new Font("Segoe UI Symbol", 20, FontStyle.Regular); using var b = new SolidBrush(accent); var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }; e.Graphics.DrawString(glyph, f, b, new RectangleF(0, 5, Width, 32), sf); using var tf = new Font("Segoe UI Semibold", 7.8f, FontStyle.Bold); e.Graphics.DrawString(title, tf, Brushes.White, new RectangleF(3, 42, Width - 6, 31), sf); }
}

sealed class GameMemoryItem : Panel
{
    readonly GameProfile profile; readonly Color good, accent;
    public GameMemoryItem(GameProfile profile, Color good, Color accent) { this.profile = profile; this.good = good; this.accent = accent; BackColor = Color.FromArgb(7, 15, 29); Cursor = Cursors.Hand; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using var border = new Pen(profile.LastScore >= 70 ? good : Color.FromArgb(25, 63, 93), 1); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1); using var iconBrush = new SolidBrush(Color.FromArgb(2, 7, 14)); e.Graphics.FillRectangle(iconBrush, 8, 9, 54, 54);
        try { if (File.Exists(profile.IconPath)) { using var img = Image.FromFile(profile.IconPath); e.Graphics.DrawImage(img, new Rectangle(9, 10, 52, 52)); } } catch { }
        using var nameFont = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold); using var infoFont = new Font("Segoe UI", 8.2f); e.Graphics.DrawString(profile.DisplayName, nameFont, Brushes.White, new RectangleF(72, 8, Width - 78, 22)); var info = $"{profile.Observations} analyses\nBest: {(string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint)}"; using var infoBrush = new SolidBrush(profile.LastScore >= 70 ? good : Color.FromArgb(135, 159, 190)); e.Graphics.DrawString(info, infoFont, infoBrush, new RectangleF(72, 31, Width - 78, 38)); using var dot = new SolidBrush(profile.LastScore >= 70 ? good : accent); e.Graphics.FillEllipse(dot, Width - 18, 10, 7, 7);
    }
}

sealed class StatusBadge : Panel
{
    public Color Accent { get; set; } = Color.LimeGreen; public string Title { get; set; } = "SYSTEM"; public string Value { get; set; } = "READY"; public StatusBadge() { BackColor = Color.FromArgb(5, 11, 23); DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p = new Pen(Color.FromArgb(90, Accent.R, Accent.G, Accent.B)); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); using var f = new Font("Segoe UI", 8, FontStyle.Bold); using var v = new Font("Segoe UI Semibold", 10, FontStyle.Bold); using var b = new SolidBrush(Color.FromArgb(135, 159, 190)); using var vb = new SolidBrush(Accent); e.Graphics.DrawString(Title, f, b, 14, 10); e.Graphics.DrawString("●  " + Value, v, vb, 14, 34); }
}

sealed class GlowProgress : Control
{
    int value; public int Value { get => value; set { this.value = Math.Clamp(value, 0, 100); Invalidate(); } } public GlowProgress() { DoubleBuffered = true; BackColor = Color.FromArgb(2, 6, 15); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); var r = new Rectangle(0, 2, Width - 1, Math.Max(5, Height - 4)); using var bg = new SolidBrush(Color.FromArgb(19, 26, 45)); e.Graphics.FillRectangle(bg, r); var w = (int)(r.Width * value / 100.0); if (w > 0) { using var g = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, w), r.Height), Color.FromArgb(155, 55, 255), Color.FromArgb(0, 224, 255), LinearGradientMode.Horizontal); e.Graphics.FillRectangle(g, new Rectangle(0, r.Y, w, r.Height)); } }
}

sealed class RadarControl : Control
{
    public RadarControl() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; var c = new Point(68, 68); using var purple = new Pen(Color.FromArgb(120, 181, 70, 255), 2); using var cyan = new Pen(Color.FromArgb(100, 0, 224, 255), 1); for (var i = 1; i <= 3; i++) e.Graphics.DrawEllipse(purple, c.X - i * 18, c.Y - i * 18, i * 36, i * 36); e.Graphics.DrawLine(cyan, 68, 8, 68, 128); e.Graphics.DrawLine(cyan, 8, 68, 128, 68); using var b = new SolidBrush(Color.FromArgb(190, 181, 70, 255)); e.Graphics.FillEllipse(b, 62, 62, 12, 12); }
}

sealed class SparklineControl : Control
{
    public List<double> Values { get; set; } = new() { 54, 57, 53, 61, 56, 63, 59, 64, 60, 66 }; public SparklineControl() { DoubleBuffered = true; BackColor = Color.FromArgb(5, 11, 22); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; using var grid = new Pen(Color.FromArgb(22, 45, 72)); for (var y = 12; y < Height; y += 20) e.Graphics.DrawLine(grid, 0, y, Width, y); if (Values.Count < 2) return; var min = Values.Min(); var max = Values.Max(); var range = Math.Max(1, max - min); var points = Values.Select((v, i) => new Point(8 + i * Math.Max(1, (Width - 16) / Math.Max(1, Values.Count - 1)), Height - 10 - (int)((v - min) / range * (Height - 24)))).ToArray(); using var line = new Pen(Color.FromArgb(34, 240, 106), 2); e.Graphics.DrawLines(line, points); using var dot = new SolidBrush(Color.FromArgb(34, 240, 106)); foreach (var p in points) e.Graphics.FillEllipse(dot, p.X - 2, p.Y - 2, 5, 5); }
}
