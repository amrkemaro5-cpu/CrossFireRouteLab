using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed partial class DashboardForm : Form
{
    static readonly Color Bg = Color.FromArgb(2, 5, 13);
    static readonly Color Surface = Color.FromArgb(6, 12, 25);
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
    readonly Label gameName = new(), gameMeta = new(), network = new(), router = new(), best = new(), metrics = new(), quality = new(), tips = new(), progressText = new(), systemText = new(), connections = new(), analysisTitle = new();
    readonly TextBox endpoint = new();
    readonly AnimatedProgress progress = new();
    readonly AnimatedRadar radar = new();
    readonly AnimatedSparkline graph = new();
    readonly List<GRLActionButton> actions = new();
    readonly List<GameProfile> memory = new();
    readonly Timer animationTimer = new() { Interval = 32 };
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab");
    GameProfile? current;
    bool busy;
    float phase;

    public DashboardForm()
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
        ApplyReferenceLayout();
        RefreshMemory();
        Log("GAME ROUTE LAB v7.0");
        Log("Smart game detection • ISP • router • endpoint • route quality • local game memory");
        Log("READ-ONLY MODE: no Windows routes, DNS, PPPoE, router settings or firmware are changed.");
        animationTimer.Tick += (_, _) =>
        {
            phase += 0.045f;
            radar.Phase = phase;
            progress.Phase = phase;
            graph.Phase = phase;
            foreach (var button in actions) button.Phase = phase;
            radar.Invalidate(); progress.Invalidate(); graph.Invalidate();
        };
        animationTimer.Start();
        FormClosed += (_, _) => animationTimer.Stop();
    }

    void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildToolbar(), 0, 1);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, ColumnCount = 3, RowCount = 1, Padding = new Padding(14, 10, 14, 8), Margin = Padding.Empty };
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
        var p = new GRLHeader { Dock = DockStyle.Fill };
        p.Controls.Add(new PictureBox { Image = Brand.CreateLogo(104), Size = new Size(112, 112), Location = new Point(28, 8), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent });
        p.Controls.Add(Label("GAME ROUTE LAB", new Point(150, 25), new Size(700, 38), 30, TextColor, true));
        p.Controls.Add(Label("SMARTER ROUTES.  BETTER PING.", new Point(154, 67), new Size(700, 22), 12, Cyan, true));
        p.Controls.Add(Label("LOCAL-FIRST GAME NETWORK ANALYZER", new Point(155, 94), new Size(700, 20), 9, Muted));
        var status = new GRLStatus { Size = new Size(250, 70), Accent = Green, Title = "SYSTEM STATUS", Value = "READY • READ-ONLY" };
        p.Controls.Add(status);
        p.Resize += (_, _) => status.Location = new Point(Math.Max(640, p.ClientSize.Width - status.Width - 28), 25);
        return p;
    }

    Control BuildToolbar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(3, 7, 16), Padding = new Padding(12, 7, 12, 6) };
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, BackColor = Color.Transparent, Padding = new Padding(0, 1, 0, 0), Margin = Padding.Empty };
        flow.Controls.Add(Label("ENDPOINT", Point.Empty, new Size(62, 76), 8, Muted, true, ContentAlignment.MiddleLeft));
        endpoint.Width = 172; endpoint.Height = 36; endpoint.Margin = new Padding(0, 18, 8, 0); endpoint.BackColor = Color.FromArgb(9, 18, 35); endpoint.ForeColor = TextColor; endpoint.BorderStyle = BorderStyle.FixedSingle; endpoint.PlaceholderText = "Optional IP / hostname"; flow.Controls.Add(endpoint);
        AddAction(flow, GRLIcon.Radar, "AUTO ANALYZE", AutoAnalyze, Purple);
        AddAction(flow, GRLIcon.Gamepad, "REFRESH GAMES", RefreshGames, Cyan);
        AddAction(flow, GRLIcon.Network, "DETECT NETWORK", DetectNetwork, Cyan);
        AddAction(flow, GRLIcon.Router, "DETECT ROUTER", DetectRouter, Purple);
        AddAction(flow, GRLIcon.Search, "FIND CONNECTIONS", FindConnections, Cyan);
        AddAction(flow, GRLIcon.Route, "ROUTE TABLE", RouteTable, Blue);
        AddAction(flow, GRLIcon.Ping, "PING 30x", Ping30, Green);
        AddAction(flow, GRLIcon.Trace, "TRACEROUTE", Traceroute, Purple);
        AddAction(flow, GRLIcon.Chart, "PATH QUALITY", PathQuality, Green);
        AddAction(flow, GRLIcon.Report, "SAVE REPORT", SaveReport, Purple);
        bar.Controls.Add(flow);
        return bar;
    }

    Control BuildLeft()
    {
        var panel = new GRLCard { Dock = DockStyle.Fill, Accent = Purple };
        panel.Controls.Add(Label("GAME MEMORY", new Point(16, 14), new Size(220, 26), 12, Purple, true));
        panel.Controls.Add(Label("YOUR LOCAL HISTORY", new Point(16, 39), new Size(220, 20), 8.5f, Muted, true));
        games.Location = new Point(10, 66); games.Size = new Size(240, 510); games.FlowDirection = FlowDirection.TopDown; games.WrapContents = false; games.AutoScroll = true; games.BackColor = Color.Transparent; games.BorderStyle = BorderStyle.None; panel.Controls.Add(games);
        var all = new GRLActionButton { Text = "VIEW ALL GAMES", Icon = GRLIcon.Gamepad, Accent = Cyan, Location = new Point(16, 588), Size = new Size(220, 38) }; all.Click += (_, _) => AllGames(); panel.Controls.Add(all);
        var clear = new GRLActionButton { Text = "CLEAR MEMORY", Icon = GRLIcon.Trash, Accent = Purple, Location = new Point(16, 636), Size = new Size(220, 38) }; clear.Click += (_, _) => ClearStoredMemory(); panel.Controls.Add(clear);
        return panel;
    }

    Control BuildCenter()
    {
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, RowCount = 4, ColumnCount = 1, Margin = Padding.Empty };
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
        center.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var hero = new GRLCard { Dock = DockStyle.Fill, Accent = Purple };
        radar.Size = new Size(150, 150); radar.Location = new Point(18, 27); radar.BackColor = Surface; hero.Controls.Add(radar);
        analysisTitle.Text = "AUTO ANALYSIS READY"; analysisTitle.Location = new Point(190, 18); analysisTitle.Size = new Size(620, 36); analysisTitle.Font = new Font("Segoe UI Semibold", 21, FontStyle.Bold); analysisTitle.ForeColor = Purple; hero.Controls.Add(analysisTitle);
        var sub = Label("Detecting the game, connections and route quality automatically...", new Point(190, 58), new Size(760, 24), 10.5f, Muted); hero.Controls.Add(sub);
        progress.Location = new Point(190, 90); progress.Size = new Size(780, 14); hero.Controls.Add(progress);
        progressText.Text = "READY"; progressText.Location = new Point(990, 84); progressText.Size = new Size(70, 24); progressText.ForeColor = TextColor; progressText.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold); hero.Controls.Add(progressText);
        var steps = new[] { "DETECT GAME", "FIND CONNECTIONS", "TEST ENDPOINTS", "ANALYZE ROUTES", "GENERATE REPORT" };
        for (var i = 0; i < steps.Length; i++) hero.Controls.Add(Label($"{i + 1}", new Point(225 + i * 135, 126), new Size(24, 24), 9, i < 3 ? Green : i == 3 ? Purple : Muted, true, ContentAlignment.MiddleCenter));
        for (var i = 0; i < steps.Length; i++) hero.Controls.Add(Label(steps[i], new Point(195 + i * 135, 154), new Size(100, 28), 7.5f, TextColor, true, ContentAlignment.MiddleCenter));
        center.Controls.Add(hero, 0, 0);

        var summary = new GRLCard { Dock = DockStyle.Fill, Accent = Cyan };
        summary.Controls.Add(Label("CURRENT ANALYSIS SUMMARY", new Point(18, 14), new Size(400, 28), 12, Cyan, true));
        gameName.Text = "No game detected"; gameName.Location = new Point(20, 56); gameName.Size = new Size(500, 34); gameName.Font = new Font("Segoe UI Semibold", 17, FontStyle.Bold); gameName.ForeColor = TextColor; summary.Controls.Add(gameName);
        gameMeta.Text = "Start an online game and run AUTO ANALYZE again."; gameMeta.Location = new Point(20, 94); gameMeta.Size = new Size(520, 80); gameMeta.ForeColor = Muted; summary.Controls.Add(gameMeta);
        connections.Text = "No public game endpoints found yet."; connections.Location = new Point(550, 56); connections.Size = new Size(500, 120); connections.ForeColor = TextColor; connections.Font = new Font("Cascadia Mono", 9); summary.Controls.Add(connections);
        center.Controls.Add(summary, 0, 1);

        var result = new GRLCard { Dock = DockStyle.Fill, Accent = Green };
        result.Controls.Add(Label("BEST ENDPOINT (CURRENT)", new Point(18, 14), new Size(400, 28), 12, Cyan, true));
        best.Text = "—"; best.Location = new Point(20, 52); best.Size = new Size(520, 42); best.Font = new Font("Segoe UI Semibold", 15, FontStyle.Bold); best.ForeColor = TextColor; result.Controls.Add(best);
        metrics.Text = "LATENCY     — ms\r\nLOSS        —\r\nJITTER      — ms\r\nSTABILITY   —"; metrics.Location = new Point(20, 98); metrics.Size = new Size(520, 90); metrics.Font = new Font("Cascadia Mono", 9.5f); metrics.ForeColor = Green; result.Controls.Add(metrics);
        quality.Text = "WAITING"; quality.Location = new Point(780, 18); quality.Size = new Size(240, 32); quality.Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold); quality.ForeColor = Muted; quality.TextAlign = ContentAlignment.TopRight; result.Controls.Add(quality);
        graph.Size = new Size(520, 100); graph.Location = new Point(540, 70); graph.BackColor = Surface; result.Controls.Add(graph);
        center.Controls.Add(result, 0, 2);

        var consoleCard = new GRLCard { Dock = DockStyle.Fill, Accent = Blue };
        consoleCard.Controls.Add(Label("LIVE ANALYSIS CONSOLE", new Point(18, 12), new Size(400, 26), 11, Cyan, true));
        console.Dock = DockStyle.Fill; console.Location = new Point(12, 42); console.BackColor = Color.FromArgb(1, 4, 10); console.ForeColor = TextColor; console.BorderStyle = BorderStyle.None; console.ReadOnly = true; console.WordWrap = false; console.ScrollBars = RichTextBoxScrollBars.Both; console.Font = new Font("Cascadia Mono", 9); consoleCard.Controls.Add(console);
        center.Controls.Add(consoleCard, 0, 3);
        return center;
    }

    Control BuildRight()
    {
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Bg, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
        right.RowStyles.Add(new RowStyle(SizeType.Absolute, 220)); right.RowStyles.Add(new RowStyle(SizeType.Absolute, 220)); right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(new GRLInfoCard { Dock = DockStyle.Fill, Accent = Cyan, Title = "NETWORK INFORMATION", Content = "Waiting for network detection…" }, 0, 0);
        right.Controls.Add(new GRLInfoCard { Dock = DockStyle.Fill, Accent = Purple, Title = "ROUTER INTELLIGENCE", Content = "Waiting for router detection…" }, 0, 1);
        right.Controls.Add(new GRLInfoCard { Dock = DockStyle.Fill, Accent = Green, Title = "TIPS", Content = "Run analysis while the game is in an online match.\r\n\r\nMore observations improve local game memory.\r\n\r\nICMP-blocked servers are not automatically treated as packet loss.\r\n\r\nThe analyzer never changes your router or Windows routes." }, 0, 2);
        return right;
    }

    Control BuildFooter()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(3, 7, 16) };
        p.Controls.Add(Label("Game Route Lab v7.0  •  READ-ONLY MODE", new Point(18, 8), new Size(300, 22), 9.2f, Green, true)); p.Controls.Add(Label("SYSTEM: Windows 64-bit", new Point(330, 8), new Size(250, 22), 9.2f, Cyan, true));
        var ready = Label("●  READY", Point.Empty, new Size(90, 22), 9.2f, Green, true, ContentAlignment.MiddleRight); p.Controls.Add(ready); p.Resize += (_, _) => ready.Location = new Point(p.ClientSize.Width - ready.Width - 18, 8); return p;
    }

    Label Label(string text, Point location, Size size, float font, Color color, bool bold = false, ContentAlignment align = ContentAlignment.TopLeft)
        => new() { Text = text, Location = location, Size = size, Font = new Font("Segoe UI", font, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color, BackColor = Color.Transparent, TextAlign = align };

    async Task Safe(Func<Task> action)
    {
        if (busy) return; busy = true; foreach (var a in actions) a.Enabled = false;
        try { SetProgress(5, "5%"); await action(); }
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
        if (gamesFound.Count == 0)
        {
            gameName.Text = "No game detected"; gameMeta.Text = "Start an online game and run AUTO ANALYZE again."; connections.Text = "No public game endpoints found yet.\r\n\r\nThe scanner ignores ChatGPT, browsers, launchers and normal Windows services."; quality.Text = "WAITING"; quality.ForeColor = Muted; analysisTitle.Text = "AUTO ANALYSIS READY"; Log("STOP: no high-confidence game was detected. No non-game application was added to memory."); return;
        }
        current = gamesFound[0]; gameName.Text = current.DisplayName; gameMeta.Text = $"{current.Observations} saved analyses\r\nPath: {current.ExecutablePath}\r\nLast best: {(string.IsNullOrWhiteSpace(current.LastBestEndpoint) ? "—" : current.LastBestEndpoint)}"; SetProgress(48, "48%");
        Log($"GAME: {current.DisplayName} | executable identified | saved memory loaded");
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
        quality.Text = result.Stability.ToUpperInvariant(); quality.ForeColor = result.Stability == "Excellent" ? Green : result.Stability == "Good" ? Yellow : result.Stability == "Unknown" ? Muted : Red;
        graph.Values = result.Probe.History.Count > 0 ? result.Probe.History : new List<double> { 1 }; graph.Invalidate();
    }

    async Task RefreshGames()
    {
        var found = await DiscoverGames(); Log($"\r\n=== GAME DISCOVERY: {found.Count} candidate(s) ==="); foreach (var g in found) Log($"{g.DisplayName} | {g.ProcessName} | {g.Observations} saved observations | {g.ExecutablePath}");
    }

    async Task<List<GameProfile>> DiscoverGames()
    {
        var items = await GameScanner.DiscoverAsync();
        var candidates = items.Where(x => x.LikelyGame && !GameProfileStore.IsBlocked(x.ProcessName)).GroupBy(x => new { x.Pid, x.ProcessName, x.ExecutablePath }).Select(g => g.OrderByDescending(x => x.Confidence).First()).OrderByDescending(x => x.Confidence).Take(12).ToList();
        foreach (var g in candidates) { try { GameProfileStore.Touch(g.ProcessName, g.ExecutablePath); } catch { } }
        memory.Clear(); memory.AddRange(GameProfileStore.Load()); RefreshMemory();
        return candidates.Select(g => memory.FirstOrDefault(p => p.ProcessName.Equals(g.ProcessName, StringComparison.OrdinalIgnoreCase) && p.ExecutablePath.Equals(g.ExecutablePath, StringComparison.OrdinalIgnoreCase))).Where(p => p != null).Cast<GameProfile>().ToList();
    }

    async Task DetectNetwork()
    {
        var n = await NetworkProfileDetector.DetectAsync(); network.Text = $"ISP\t{n.ISP}\r\nASN\t{n.ASN}\r\nPublic IP\t{n.PublicIp}\r\nLocation\t{n.City}, {n.Country}\r\nConnection\t{n.WanType}\r\nDNS\t{n.DnsServers}"; systemText.Text = $"SYSTEM: Windows {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}  •  {n.InterfaceName}"; Log($"NETWORK: ISP={n.ISP} | Org={n.Organization} | ASN={n.ASN} | Public={n.PublicIp} | GW={n.Gateway}");
    }

    async Task DetectRouter()
    {
        var r = await RouterDetector.DetectAsync(); router.Text = $"Gateway\t{r.Gateway}\r\nManufacturer\t{r.Vendor}\r\nModel\t{r.Model}\r\nFirmware\t{r.Firmware}\r\nInterface\t{r.ManagementUrl}\r\nConfidence\t{r.Confidence}"; Log($"ROUTER: {r.Vendor} {r.Model} | firmware {r.Firmware} | confidence {r.Confidence}");
    }

    async Task FindConnections()
    {
        var found = await GameScanner.DiscoverAsync(); var game = found.FirstOrDefault(x => x.LikelyGame); if (game == null) { Log("No high-confidence game connection found. ChatGPT and browser processes are excluded."); return; }
        var eps = await GetEndpoints(game.Pid); connections.Text = eps.Count == 0 ? "Game detected, but no public established sockets visible." : string.Join("\r\n", eps.Select(x => $"{x.Protocol}  {x.RemoteIp}:{x.RemotePort}   {x.State}")); foreach (var ep in eps) Log($"{ep.Protocol} {ep.RemoteIp}:{ep.RemotePort} {ep.State}");
    }

    async Task RouteTable() => Log("\r\n=== ROUTE TABLE ===\r\n" + await Run("route.exe", "print", 10000));
    async Task Ping30() { var ip = Target(); if (ip.Length == 0) { Log("No endpoint selected."); return; } Log($"\r\n=== PING 30x {ip} ===\r\n" + await Run("ping.exe", $"-n 30 {ip}", 50000)); }
    async Task Traceroute() { var ip = Target(); if (ip.Length == 0) { Log("No endpoint selected."); return; } Log($"\r\n=== TRACEROUTE {ip} ===\r\n" + await Run("tracert.exe", $"-d -h 30 -w 700 {ip}", 45000)); }

    async Task PathQuality()
    {
        var ip = Target(); if (ip.Length == 0) { Log("No endpoint selected."); return; }
        var p = await Probe(ip); var t = await Trace(ip); var ep = new GameEndpoint("manual", 0, "TCP", ip, 0, "MANUAL", false, 0, ""); ApplyResult(new RouteResult(ep, p, t));
        Log($"PATH QUALITY: {(p.HasResponse ? p.Avg.ToString("0") : "blocked/unknown")} ms | loss {(p.HasResponse ? p.Loss.ToString("0") : "unknown")} | jitter {(p.HasResponse ? p.Jitter.ToString("0") : "unknown")} | hops {t.Hops}");
    }

    async Task SaveReport()
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"GameRouteLab_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var text = console.Text + Environment.NewLine + "=== CURRENT RESULT ===" + Environment.NewLine + best.Text + Environment.NewLine + metrics.Text; await File.WriteAllTextAsync(file, text); Log("Report saved: " + file);
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
        return all
            .Where(x => x.Pid == pid && x.LikelyGame && x.RemotePort > 0 && IPAddress.TryParse(x.RemoteIp, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .GroupBy(x => $"{x.Protocol}|{x.RemoteIp}|{x.RemotePort}")
            .Select(g => g.First())
            .ToList();
    }

    async Task<ProbeResult> Probe(string host)
    {
        var samples = new List<long>();
        for (var i = 0; i < 5; i++) { try { using var ping = new Ping(); var r = await ping.SendPingAsync(host, 900); if (r.Status == IPStatus.Success) samples.Add(r.RoundtripTime); } catch { } }
        if (samples.Count == 0) return new ProbeResult(0, 0, 0, new List<double>(), false);
        var avg = samples.Average(); var jitter = samples.Count < 2 ? 0 : samples.Zip(samples.Skip(1), (a, b) => Math.Abs(a - b)).Average(); return new ProbeResult(avg, (5 - samples.Count) * 20, jitter, samples.Select(x => (double)x).ToList(), true);
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
            using var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } }; p.Start();
            var outputTask = p.StandardOutput.ReadToEndAsync(); var errorTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout)) { try { p.Kill(true); } catch { } return await outputTask + Environment.NewLine + await errorTask + Environment.NewLine + "[Timed out]"; }
            return await outputTask + Environment.NewLine + await errorTask;
        }
        catch (Exception ex) { return "[Command error] " + ex.Message; }
    }

    void RefreshMemory()
    {
        games.SuspendLayout(); games.Controls.Clear();
        var clean = memory.Where(g => !GameProfileStore.IsBlocked(g.ProcessName)).OrderByDescending(x => x.LastSeenUtc).Take(8).ToList();
        foreach (var g in clean)
        {
            var item = new GameMemoryItem(g, Green, Cyan) { Width = Math.Max(205, games.ClientSize.Width - 8), Height = 78, Margin = new Padding(0, 2, 0, 5) }; item.Click += (_, _) => SelectGame(g); games.Controls.Add(item);
        }
        if (clean.Count == 0) games.Controls.Add(Label("No games remembered yet.\r\n\r\nStart a game and click\r\nAUTO ANALYZE.", Point.Empty, new Size(220, 100), 9.5f, Muted));
        games.ResumeLayout();
    }

    void SelectGame(GameProfile profile)
    {
        if (GameProfileStore.IsBlocked(profile.ProcessName)) return; current = profile; gameName.Text = profile.DisplayName; gameMeta.Text = $"{profile.Observations} saved analyses\r\nPath: {profile.ExecutablePath}\r\nBest: {(string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint)}"; best.Text = string.IsNullOrWhiteSpace(profile.LastBestEndpoint) ? "—" : profile.LastBestEndpoint; Log($"\r\n[MEMORY] {profile.DisplayName}\r\nObservations: {profile.Observations}\r\nLast best: {profile.LastBestEndpoint}");
    }

    void AllGames()
    {
        using var f = new Form { Text = "Game Route Lab • All Games", Size = new Size(760, 560), MinimumSize = new Size(600, 420), BackColor = Bg, StartPosition = FormStartPosition.CenterParent, ForeColor = TextColor };
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = Surface, ForeColor = TextColor, Font = new Font("Segoe UI", 10.5f), BorderStyle = BorderStyle.None };
        foreach (var g in memory.Where(x => !GameProfileStore.IsBlocked(x.ProcessName)).OrderByDescending(x => x.LastSeenUtc)) list.Items.Add($"{g.DisplayName}    •    {g.Observations} analyses    •    Best {g.LastBestEndpoint}");
        f.Controls.Add(list); f.ShowDialog(this);
    }

    void ClearStoredMemory()
    {
        try { var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab"); var file = Path.Combine(root, "profiles.json"); if (File.Exists(file)) File.Delete(file); memory.Clear(); current = null; RefreshMemory(); Log("Game memory cleared."); } catch (Exception ex) { Log("[ERROR] " + ex.Message); }
    }

    void Log(string text) { if (InvokeRequired) { BeginInvoke(() => Log(text)); return; } console.AppendText(text + Environment.NewLine); console.SelectionStart = console.TextLength; console.ScrollToCaret(); }

    record struct RouteResult(GameEndpoint Endpoint, ProbeResult Probe, TraceResult Trace)
    {
        public double Score => (Probe.Avg > 0 ? Probe.Avg : 999) + Trace.Hops * 2;
        public string Stability => Probe.HasResponse ? (Probe.Avg < 50 && Probe.Jitter < 8 ? "Excellent" : Probe.Avg < 80 ? "Good" : "Fair") : "Unknown";
    }
    record struct ProbeResult(double Avg, double Loss, double Jitter, List<double> History, bool HasResponse);
    record struct TraceResult(int Hops, double Last);
}

sealed class GRLHeader : Panel
{
    public GRLHeader() { DoubleBuffered = true; BackColor = Color.FromArgb(2, 5, 13); Paint += (_, e) => { using var b = new LinearGradientBrush(new Rectangle(0, ClientSize.Height - 4, ClientSize.Width, 4), Color.FromArgb(181, 70, 255), Color.FromArgb(0, 224, 255), LinearGradientMode.Horizontal); e.Graphics.FillRectangle(b, 0, ClientSize.Height - 4, ClientSize.Width, 4); }; }
}

sealed class GRLCard : Panel
{
    public Color Accent { get; set; }
    public GRLCard() { DoubleBuffered = true; BackColor = Color.FromArgb(6, 12, 25); Padding = Padding.Empty; Paint += (_, e) => { using var p = new Pen(Color.FromArgb(16, 54, 88)); e.Graphics.DrawRectangle(p, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1)); using var a = new Pen(Color.FromArgb(Accent.R, Accent.G, Accent.B, 130)); e.Graphics.DrawLine(a, 0, 0, ClientSize.Width, 0); }; }
}

sealed class GRLInfoCard : Panel
{
    public string Title { get; set; } = ""; public string Content { get; set; } = ""; public Color Accent { get; set; }
    public GRLInfoCard() { DoubleBuffered = true; BackColor = Color.FromArgb(6, 12, 25); Paint += (_, e) => { using var p = new Pen(Color.FromArgb(16, 54, 88)); e.Graphics.DrawRectangle(p, 0, 0, Math.Max(0, ClientSize.Width - 1), Math.Max(0, ClientSize.Height - 1)); using var b = new SolidBrush(Accent); e.Graphics.DrawString(Title, new Font("Segoe UI Semibold", 11, FontStyle.Bold), b, 16, 15); using var t = new SolidBrush(Color.FromArgb(205, 220, 238)); e.Graphics.DrawString(Content, new Font("Cascadia Mono", 8.8f), t, new RectangleF(16, 48, ClientSize.Width - 32, ClientSize.Height - 58)); }; }
}

sealed class GRLStatus : Panel
{
    public string Title { get; set; } = ""; public string Value { get; set; } = ""; public Color Accent { get; set; }
    public GRLStatus() { BackColor = Color.FromArgb(4, 10, 20); Paint += (_, e) => { using var p = new Pen(Color.FromArgb(25, 75, 95)); e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1); using var b = new SolidBrush(Color.FromArgb(155, 175, 200)); e.Graphics.DrawString(Title, new Font("Segoe UI", 8), b, 14, 12); using var a = new SolidBrush(Accent); e.Graphics.DrawString("●  " + Value, new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold), a, 14, 35); }; }
}

sealed class GRLActionButton : Button
{
    public object Icon { get; set; } = GRLIcon.Gamepad; public Color Accent { get; set; } = Color.White; public float Phase { get; set; }
    public GRLActionButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderColor = Color.FromArgb(20, 60, 95); FlatAppearance.MouseOverBackColor = Color.FromArgb(12, 28, 52); BackColor = Color.FromArgb(5, 12, 24); ForeColor = Color.FromArgb(230, 240, 250); Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold); Cursor = Cursors.Hand; DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p = new Pen(Color.FromArgb(Accent.R, Accent.G, Accent.B, 160), 1); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
}

sealed class AnimatedProgress : Control
{
    public int Value { get; set; } public float Phase { get; set; }
    public AnimatedProgress() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) { using var bg = new SolidBrush(Color.FromArgb(10, 18, 35)); e.Graphics.FillRectangle(bg, ClientRectangle); var w = (int)(ClientSize.Width * Math.Clamp(Value / 100f, 0, 1)); if (w > 0) { using var b = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, w), Height), Color.FromArgb(181, 70, 255), Color.FromArgb(0, 224, 255), LinearGradientMode.Horizontal); e.Graphics.FillRectangle(b, 0, 0, w, Height); } }
}

sealed class AnimatedRadar : Control
{
    public float Phase { get; set; } public AnimatedRadar() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) { var c = new PointF(Width / 2f, Height / 2f); using var p = new Pen(Color.FromArgb(181, 70, 255), 1); for (var i = 1; i <= 4; i++) { var r = i * Math.Min(Width, Height) / 10f; e.Graphics.DrawEllipse(p, c.X - r, c.Y - r, r * 2, r * 2); } var a = Phase; using var beam = new Pen(Color.FromArgb(181, 70, 255), 2); e.Graphics.DrawLine(beam, c, new PointF(c.X + (float)Math.Cos(a) * Width * .45f, c.Y + (float)Math.Sin(a) * Height * .45f)); using var b = new SolidBrush(Color.FromArgb(0, 224, 255)); e.Graphics.FillEllipse(b, c.X - 5, c.Y - 5, 10, 10); }
}

sealed class AnimatedSparkline : Control
{
    public List<double> Values { get; set; } = new(); public float Phase { get; set; } public AnimatedSparkline() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) { using var grid = new Pen(Color.FromArgb(20, 45, 70)); for (var y = 15; y < Height; y += 24) e.Graphics.DrawLine(grid, 0, y, Width, y); var v = Values.Count > 1 ? Values : Enumerable.Range(0, 10).Select(i => 55 + 10 * Math.Sin(i)).ToList(); var min = v.Min(); var max = v.Max(); var span = Math.Max(1, max - min); var pts = v.Select((x, i) => new PointF(i * (Width - 4f) / Math.Max(1, v.Count - 1) + 2, Height - 8 - (float)((x - min) / span) * (Height - 20))).ToArray(); using var pen = new Pen(Color.FromArgb(34, 240, 106), 2); if (pts.Length > 1) e.Graphics.DrawLines(pen, pts); foreach (var pt in pts) e.Graphics.FillEllipse(Brushes.Lime, pt.X - 2, pt.Y - 2, 4, 4); }
}



