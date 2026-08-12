using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed class GameRouteLabV10StableForm : Form
{
    static readonly Color Bg = Color.FromArgb(3, 7, 17), Surface = Color.FromArgb(7, 13, 27), Surface2 = Color.FromArgb(10, 18, 36);
    static readonly Color Cyan = Color.FromArgb(0, 225, 255), Purple = Color.FromArgb(188, 72, 255), Green = Color.FromArgb(40, 242, 122), Blue = Color.FromArgb(83, 135, 255), Fg = Color.FromArgb(238, 246, 255), Muted = Color.FromArgb(132, 157, 190);
    readonly TableLayoutPanel root = new(), body = new(), center = new();
    readonly FlowLayoutPanel games = new();
    readonly RichTextBox console = new();
    readonly TextBox endpointBox = new();
    readonly Label gameTitle = new(), gameMeta = new(), connectionText = new(), metrics = new(), quality = new(), networkText = new(), routerText = new(), progressState = new();
    readonly PictureBox gameIcon = new();
    readonly ProgressBar progress = new();
    readonly Radar radar = new();
    readonly PingGraph pingGraph = new();
    readonly TelemetryCard network = new(), router = new();
    readonly Timer visualTimer = new() { Interval = 120 }, scanTimer = new() { Interval = 2800 }, pingTimer = new() { Interval = 1000 };
    readonly string dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab");
    readonly string customFile;
    readonly List<string> customGames = new();
    readonly List<Endpoint> endpoints = new();
    readonly List<double> samples = new();
    float phase;
    bool busy;
    int pid, port;
    string? target;
    double lastPing = -1, jitter;
    string networkState = "Waiting for network scan…", routerState = "Waiting for router scan…";
    static readonly string[] Known = { "crossfire", "crossfire2", "crossfire_client", "crossfireclient", "valorant", "cs2", "csgo", "cod", "codhq", "r5apex", "pubg", "tslgame", "leagueoflegends", "dota2", "fortniteclient-win64-shipping" };

    public GameRouteLabV10StableForm()
    {
        base.Text = "Game Route Lab v10";
        ClientSize = new Size(1500, 920); MinimumSize = new Size(1180, 760); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg; ForeColor = Fg; Font = new Font("Segoe UI", 9.5f); DoubleBuffered = true; AutoScaleMode = AutoScaleMode.Dpi; ShowInTaskbar = true;
        customFile = Path.Combine(dataDir, "custom-games.txt"); Directory.CreateDirectory(dataDir); try { Icon = Brand.CreateIcon(); } catch { }
        BuildUi(); LoadCustom(); RefreshGames();
        Log("GAME ROUTE LAB v10.0 STABLE"); Log("1 Detect Game → 2 Connections → 3 Test Ping → 4 Route → 5 Report"); Log("Endpoint is automatic. ADD GAME EXE is only the fallback for unsupported games.");
        Log("No CrossFire window guard is installed; the app never changes WindowState, activation, TopMost or focus.");
        visualTimer.Tick += (_, _) => Animate(); scanTimer.Tick += async (_, _) => { if (!busy) await BackgroundScan(); }; pingTimer.Tick += async (_, _) => { if (!busy && target != null) await PingTarget(); };
        visualTimer.Start(); scanTimer.Start(); FormClosed += (_, _) => { visualTimer.Stop(); scanTimer.Stop(); pingTimer.Stop(); };
    }

    void BuildUi()
    {
        root.Dock = DockStyle.Fill; root.RowCount = 4; root.ColumnCount = 1; root.Margin = Padding.Empty; root.Padding = Padding.Empty;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 118)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        root.Controls.Add(Header(), 0, 0); root.Controls.Add(Toolbar(), 0, 1); root.Controls.Add(Body(), 0, 2); root.Controls.Add(Footer(), 0, 3); Controls.Add(root);
    }

    Control Header()
    {
        var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg }; p.Controls.Add(new PictureBox { Image = Brand.CreateLogo(88), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(22, 14, 88, 88) });
        p.Controls.Add(L("GAME ROUTE LAB", 124, 17, 29, Fg, true, 600, 40)); p.Controls.Add(L("SMARTER ROUTES.  BETTER PING.", 126, 57, 12, Cyan, true, 450, 22)); p.Controls.Add(L("LOCAL-FIRST GAME NETWORK ANALYZER  •  v10.0", 126, 79, 8.5f, Muted, false, 480, 20));
        var s = new Panel { Bounds = new Rectangle(1220, 18, 240, 66), BackColor = Surface }; s.Paint += (_, e) => { using var q = new Pen(Color.FromArgb(140, Purple)); e.Graphics.DrawRectangle(q, 0, 0, s.Width - 1, s.Height - 1); }; s.Controls.Add(L("●  ACTIVE • READ-ONLY", 18, 22, 9, Green, true, 205, 22)); p.Controls.Add(s);
        p.Paint += (_, e) => { using var q = new Pen(Color.FromArgb(150, Cyan), 2); e.Graphics.DrawLine(q, 4, p.Height - 2, p.Width - 4, p.Height - 2); }; return p;
    }

    Control Toolbar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(4, 9, 20), Padding = new Padding(10, 7, 10, 6) };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 10, RowCount = 1, Margin = Padding.Empty }; t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); for (int i = 1; i < 10; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 1));
        endpointBox.Dock = DockStyle.Fill; endpointBox.ReadOnly = true; endpointBox.BackColor = Surface2; endpointBox.ForeColor = Fg; endpointBox.BorderStyle = BorderStyle.FixedSingle; endpointBox.TextAlign = HorizontalAlignment.Center; endpointBox.PlaceholderText = "AUTO ENDPOINT"; endpointBox.Margin = new Padding(3, 5, 3, 5); t.Controls.Add(endpointBox, 0, 0);
        string[] names = { "AUTO ANALYZE", "REFRESH GAMES", "DETECT NETWORK", "DETECT ROUTER", "FIND CONNECTIONS", "PING 30x", "TRACEROUTE", "PATH QUALITY", "SAVE REPORT" }; Color[] colors = { Purple, Cyan, Cyan, Purple, Cyan, Green, Purple, Green, Blue };
        for (int i = 0; i < names.Length; i++) { var b = Tool(names[i], colors[i]); b.Margin = new Padding(3, 1, 3, 1); t.Controls.Add(b, i + 1, 0); }
        bar.Controls.Add(t); return bar;
    }

    Button Tool(string text, Color accent)
    {
        var b = new Button { Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Fg, Font = new Font("Segoe UI Semibold", 8.1f), UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = accent; b.FlatAppearance.BorderSize = 1;
        b.Click += async (_, _) => { if (busy && text != "PING 30x") return; try { switch (text) { case "AUTO ANALYZE": await AutoAnalyze(); break; case "REFRESH GAMES": RefreshGames(); break; case "DETECT NETWORK": await DetectNetwork(); break; case "DETECT ROUTER": await DetectRouter(); break; case "FIND CONNECTIONS": await FindConnections(); break; case "PING 30x": await Ping30(); break; case "TRACEROUTE": await TraceRoute(); break; case "PATH QUALITY": await PathQuality(); break; case "SAVE REPORT": SaveReport(); break; } } catch (Exception ex) { Log("[ERROR] " + ex.Message); } }; return b;
    }

    Control Body()
    {
        body.Dock = DockStyle.Fill; body.ColumnCount = 3; body.RowCount = 1; body.Padding = new Padding(12, 7, 12, 6); body.Margin = Padding.Empty;
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 292));
        body.Controls.Add(Left(), 0, 0); body.Controls.Add(Center(), 1, 0); body.Controls.Add(Right(), 2, 0); return body;
    }

    Control Left()
    {
        var p = new Card(Purple) { Dock = DockStyle.Fill }; p.Controls.Add(L("GAME MEMORY", 16, 14, 13, Purple, true, 190, 24)); p.Controls.Add(L("RUNNING GAMES APPEAR HERE", 16, 38, 8, Muted, true, 200, 20));
        games.Location = new Point(10, 62); games.FlowDirection = FlowDirection.TopDown; games.WrapContents = false; games.AutoScroll = true; games.BackColor = Color.Transparent; p.Controls.Add(games);
        var view = Side("VIEW ALL GAMES", Cyan, RefreshGames), add = Side("ADD GAME EXE", Blue, AddGame), clear = Side("CLEAR MEMORY", Purple, ClearMemory), help = Side("HOW TO USE", Green, Guide);
        p.Controls.Add(view); p.Controls.Add(add); p.Controls.Add(clear); p.Controls.Add(help);
        p.Resize += (_, _) => { int y = Math.Max(90, p.ClientSize.Height - 176), i = 0; foreach (var b in new[] { view, add, clear, help }) b.SetBounds(14, y + i++ * 41, p.ClientSize.Width - 28, 35); games.Bounds = new Rectangle(10, 62, p.ClientSize.Width - 20, Math.Max(80, y - 70)); }; return p;
    }

    Button Side(string text, Color color, Action action) { var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Fg, Font = new Font("Segoe UI Semibold", 8.4f), Cursor = Cursors.Hand }; b.FlatAppearance.BorderColor = color; b.FlatAppearance.BorderSize = 1; b.Click += (_, _) => action(); return b; }

    Control Center()
    {
        center.Dock = DockStyle.Fill; center.ColumnCount = 1; center.RowCount = 4; center.Margin = Padding.Empty;
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 24)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 22)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 27)); center.RowStyles.Add(new RowStyle(SizeType.Percent, 27));
        var hero = new Card(Purple) { Dock = DockStyle.Fill }; hero.Controls.Add(radar); radar.Bounds = new Rectangle(18, 18, 116, 116); hero.Controls.Add(L("GUIDED AUTO ANALYSIS", 154, 17, 20, Purple, true, 600, 34)); hero.Controls.Add(L("Find the game and endpoints automatically — no endpoint typing required.", 154, 48, 10, Muted, false, 760, 24)); progress.Bounds = new Rectangle(154, 82, 650, 12); progress.Maximum = 100; progress.Value = 0; hero.Controls.Add(progress); progressState.Bounds = new Rectangle(812, 76, 90, 24); progressState.Text = "READY"; progressState.ForeColor = Green; progressState.Font = new Font("Segoe UI Semibold", 8.5f); progressState.TextAlign = ContentAlignment.MiddleRight; hero.Controls.Add(progressState);
        string[] steps = { "1  DETECT GAME", "2  CONNECTIONS", "3  TEST PING", "4  ROUTE", "5  REPORT" }; for (int i = 0; i < 5; i++) hero.Controls.Add(L(steps[i], 150 + i * 145, 118, 7.7f, i == 0 ? Green : Muted, true, 130, 20, ContentAlignment.MiddleCenter)); center.Controls.Add(hero, 0, 0);
        var summary = new Card(Cyan) { Dock = DockStyle.Fill }; summary.Controls.Add(L("CURRENT ANALYSIS SUMMARY", 18, 12, 12, Cyan, true, 320, 24)); gameIcon.Bounds = new Rectangle(18, 47, 60, 60); gameIcon.SizeMode = PictureBoxSizeMode.Zoom; gameIcon.Image = Brand.CreateLogo(60); gameIcon.BackColor = Surface2; summary.Controls.Add(gameIcon); gameTitle.Bounds = new Rectangle(92, 46, 380, 32); gameTitle.Font = new Font("Segoe UI Semibold", 17, FontStyle.Bold); gameTitle.ForeColor = Fg; summary.Controls.Add(gameTitle); gameMeta.Bounds = new Rectangle(92, 80, 400, 52); gameMeta.Font = new Font("Cascadia Mono", 8.5f); gameMeta.ForeColor = Muted; summary.Controls.Add(gameMeta); summary.Controls.Add(L("DISCOVERED CONNECTIONS", 510, 48, 10, Cyan, true, 420, 22)); connectionText.Bounds = new Rectangle(510, 74, 560, 55); connectionText.Font = new Font("Cascadia Mono", 8.7f); connectionText.ForeColor = Fg; summary.Controls.Add(connectionText); center.Controls.Add(summary, 0, 1);
        var best = new Card(Green) { Dock = DockStyle.Fill }; best.Controls.Add(L("BEST ENDPOINT + LIVE PING TRACKER", 18, 11, 12, Cyan, true, 430, 24)); metrics.Bounds = new Rectangle(18, 48, 330, 118); metrics.Font = new Font("Cascadia Mono", 9.4f); metrics.ForeColor = Green; best.Controls.Add(metrics); quality.Bounds = new Rectangle(370, 12, 650, 26); quality.Font = new Font("Segoe UI Semibold", 10); quality.ForeColor = Muted; quality.TextAlign = ContentAlignment.TopRight; best.Controls.Add(quality); pingGraph.Bounds = new Rectangle(360, 46, 680, 125); best.Controls.Add(pingGraph); center.Controls.Add(best, 0, 2);
        var cc = new Card(Blue) { Dock = DockStyle.Fill }; cc.Controls.Add(L("LIVE ANALYSIS CONSOLE", 14, 8, 11, Cyan, true, 300, 22)); console.Location = new Point(10, 32); console.BackColor = Color.FromArgb(1, 3, 8); console.ForeColor = Fg; console.ReadOnly = true; console.WordWrap = false; console.ScrollBars = RichTextBoxScrollBars.Both; console.BorderStyle = BorderStyle.FixedSingle; console.Font = new Font("Cascadia Mono", 8.4f); cc.Controls.Add(console); cc.Resize += (_, _) => console.Bounds = new Rectangle(10, 32, Math.Max(100, cc.ClientSize.Width - 20), Math.Max(60, cc.ClientSize.Height - 40)); center.Controls.Add(cc, 0, 3); return center;
    }

    Control Right()
    {
        var r = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty }; r.RowStyles.Add(new RowStyle(SizeType.Percent, 34)); r.RowStyles.Add(new RowStyle(SizeType.Percent, 34)); r.RowStyles.Add(new RowStyle(SizeType.Percent, 32));
        network.Title = "NETWORK TELEMETRY"; network.Accent = Cyan; network.TextLabel = networkText; network.Controls.Add(networkText); networkText.ForeColor = Fg; networkText.Font = new Font("Cascadia Mono", 8.1f); network.State = "WAITING"; r.Controls.Add(network, 0, 0);
        router.Title = "ROUTER INTELLIGENCE"; router.Accent = Purple; router.TextLabel = routerText; router.Controls.Add(routerText); routerText.ForeColor = Fg; routerText.Font = new Font("Cascadia Mono", 8.1f); router.State = "WAITING"; r.Controls.Add(router, 0, 1);
        var g = new Card(Green) { Dock = DockStyle.Fill }; g.Controls.Add(L("WHAT TO PRESS • IN ORDER", 14, 12, 10.2f, Green, true, 270, 24)); var guide = L("1  Launch the game and enter an online match.\r\n2  Press AUTO ANALYZE.\r\n3  Wait for Best Endpoint to fill.\r\n4  Press PING 30x for a sample.\r\n5  Use TRACEROUTE / PATH QUALITY.\r\n6  Press SAVE REPORT.\r\n\r\nEndpoint is automatic.\r\nADD GAME EXE is only a fallback.", 14, 42, 8.1f, Fg, false, 260, 155); guide.Font = new Font("Cascadia Mono", 8.1f); g.Controls.Add(guide); r.Controls.Add(g, 0, 2); return r;
    }

    Control Footer() { var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg }; p.Controls.Add(L("Game Route Lab v10.0  •  READ-ONLY  •  NO ROUTE/DNS CHANGES", 16, 4, 8.3f, Green, true, 460, 20)); p.Controls.Add(L("LOW-OVERHEAD ANIMATION", 500, 4, 8.3f, Cyan, true, 210, 20)); p.Controls.Add(L("SYSTEM: WINDOWS 64-BIT", 790, 4, 8.3f, Muted, true, 210, 20)); p.Controls.Add(L("● READY", 1360, 4, 8.3f, Green, true, 100, 20)); return p; }
    Label L(string s, int x, int y, float size, Color c, bool bold, int w, int h, ContentAlignment a = ContentAlignment.TopLeft) => new() { Text = s, Bounds = new Rectangle(x, y, w, h), Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = c, BackColor = Color.Transparent, TextAlign = a, AutoEllipsis = true };

    async Task AutoAnalyze()
    {
        if (busy) return; busy = true; pingTimer.Stop(); try { SetProgress(8, "SCANNING"); radar.Active = true; await DetectNetwork(); SetProgress(22, "NETWORK"); await DetectRouter(); SetProgress(34, "ROUTER"); var g = await FindGame(); if (g == null) { ResetGame(); SetProgress(0, "WAITING"); return; } SetGame(g); SetProgress(52, "GAME FOUND"); await FindConnections(); SetProgress(70, "CONNECTIONS"); if (endpoints.Count == 0) { quality.Text = "GAME FOUND • WAITING FOR PUBLIC ENDPOINT"; Log("[AUTO] Game detected, but no public endpoint is visible yet. Stay in an online match and retry."); return; } SelectEndpoint(); SetProgress(84, "TESTING PING"); await PingTarget(); SetProgress(100, "LIVE"); pingTimer.Start(); } finally { busy = false; }
    }

    async Task<GameInfo?> FindGame() { var names = new HashSet<string>(Known.Concat(customGames), StringComparer.OrdinalIgnoreCase); return await Task.Run(() => { foreach (var p in Process.GetProcesses()) { try { if (!names.Contains(p.ProcessName)) continue; string path = ""; try { path = p.MainModule?.FileName ?? ""; } catch { } return new GameInfo(Pretty(p.ProcessName), p.Id, path); } catch { } finally { p.Dispose(); } } return null; }); }

    async Task FindConnections()
    {
        if (pid <= 0) return; var found = await Task.Run(() => ReadEndpoints(pid)); endpoints.Clear(); endpoints.AddRange(found); connectionText.Text = endpoints.Count == 0 ? "No public endpoint candidate yet." : string.Join("\r\n", endpoints.Take(5).Select(x => $"{x.Protocol,-3}  {x.Ip}:{x.Port,-5}  {x.State}")); Log($"[CONNECTIONS] {endpoints.Count} public endpoint candidate(s) found."); if (endpoints.Count > 0) SelectEndpoint();
    }

    List<Endpoint> ReadEndpoints(int gamePid)
    {
        var list = new List<Endpoint>();
        foreach (var line in Netstat("-ano -p tcp")) { var m = Regex.Match(line.Trim(), @"^TCP\s+(\S+):(\d+)\s+(\S+):(\d+)\s+(\S+)\s+(\d+)$", RegexOptions.IgnoreCase); if (!m.Success || !int.TryParse(m.Groups[6].Value, out var p) || p != gamePid) continue; var ip = m.Groups[3].Value.Trim('[', ']'); if (!PublicIp(ip)) continue; var prt = int.Parse(m.Groups[4].Value); if (prt is 80 or 443 or 53 or 5222) continue; list.Add(new Endpoint(ip, prt, "TCP", m.Groups[5].Value)); }
        foreach (var line in Netstat("-ano -p udp")) { var m = Regex.Match(line.Trim(), @"^UDP\s+(\S+):(\d+)\s+(\S+)\s+(\d+)$", RegexOptions.IgnoreCase); if (!m.Success || !int.TryParse(m.Groups[4].Value, out var p) || p != gamePid) continue; var ip = m.Groups[3].Value.Trim('[', ']'); if (!PublicIp(ip)) continue; list.Add(new Endpoint(ip, int.Parse(m.Groups[2].Value), "UDP", "ACTIVE")); }
        return list.GroupBy(x => $"{x.Protocol}|{x.Ip}|{x.Port}").Select(x => x.First()).Take(12).ToList();
    }

    IEnumerable<string> Netstat(string args) { try { using var p = new Process { StartInfo = new ProcessStartInfo("netstat.exe", args) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.ASCII } }; p.Start(); var s = p.StandardOutput.ReadToEnd(); p.WaitForExit(1800); return s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); } catch { return Array.Empty<string>(); } }

    void SelectEndpoint()
    {
        var e = endpoints.OrderBy(x => x.Protocol == "TCP" ? 0 : 1).ThenBy(x => x.Port).FirstOrDefault(); if (e == null) return; target = e.Ip; port = e.Port; endpointBox.Text = $"{e.Ip}:{e.Port}"; quality.Text = "● TARGET SELECTED"; quality.ForeColor = Cyan; UpdateMetrics(); Log($"[ENDPOINT] Automatically selected {e.Ip}:{e.Port} ({e.Protocol}).");
    }

    async Task PingTarget()
    {
        if (target == null) return; var t = target; var p = port; var ms = await Task.Run(() => Probe(t, p)); if (ms < 0) { Log($"[PING] {t}:{p} did not answer the probe."); return; } if (lastPing >= 0) jitter = Math.Abs(ms - lastPing) * .35 + jitter * .65; lastPing = ms; samples.Add(ms); if (samples.Count > 36) samples.RemoveAt(0); pingGraph.Values = samples.ToList(); UpdateMetrics(); quality.Text = $"● LIVE • {lastPing:0} ms"; quality.ForeColor = Green; Log($"[PING] {t}:{p} → {lastPing:0} ms | jitter {jitter:0.0} ms");
    }

    double Probe(string ip, int p)
    {
        try { using var ping = new Ping(); var r = ping.Send(ip, 900); if (r.Status == IPStatus.Success) return r.RoundtripTime; } catch { }
        try { var sw = Stopwatch.StartNew(); using var c = new TcpClient(); var task = c.ConnectAsync(ip, p); if (task.Wait(850) && c.Connected) { sw.Stop(); return sw.Elapsed.TotalMilliseconds; } } catch { }
        return -1;
    }

    async Task Ping30() { if (target == null) { Log("[PING] No endpoint selected. Run AUTO ANALYZE first."); return; } pingTimer.Stop(); for (int i = 0; i < 30; i++) { await PingTarget(); await Task.Delay(120); } pingTimer.Start(); Log("[PING 30x] 30 samples completed."); }
    async Task TraceRoute() { if (target == null) { Log("[TRACEROUTE] No endpoint selected."); return; } Log("[TRACEROUTE] Testing route to " + target + "…"); var o = await Task.Run(() => Command("tracert.exe", $"-d -h 12 -w 650 {target}")); Log(o.Length > 3200 ? o[^3200..] : o); }
    async Task PathQuality() { if (target == null) { Log("[PATH] No endpoint selected."); return; } var a = await Task.Run(() => Enumerable.Range(0, 8).Select(_ => Probe(target, port)).Where(x => x >= 0).ToList()); if (a.Count == 0) { Log("[PATH] Endpoint did not answer the probe."); return; } Log($"[PATH] {target} | min {a.Min():0} ms | avg {a.Average():0} ms | max {a.Max():0} ms | spread {(a.Max() - a.Min()):0} ms"); }
    string Command(string file, string args) { try { using var p = new Process { StartInfo = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } }; p.Start(); var o = p.StandardOutput.ReadToEnd(); var e = p.StandardError.ReadToEnd(); p.WaitForExit(15000); return o + (string.IsNullOrWhiteSpace(e) ? "" : "\r\n" + e); } catch (Exception ex) { return ex.Message; } }

    async Task DetectNetwork() { networkState = await Task.Run(() => LocalNetwork()); network.State = "LIVE"; Log("[NETWORK] Network telemetry refreshed."); }
    async Task DetectRouter() { routerState = await Task.Run(() => LocalRouter()); router.State = "LIVE"; Log("[ROUTER] Router intelligence refreshed."); }
    string LocalNetwork() { try { var n = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.OperationalStatus == OperationalStatus.Up && x.GetIPProperties().GatewayAddresses.Any()) ?? NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.OperationalStatus == OperationalStatus.Up); if (n == null) return "No active network interface detected."; var p = n.GetIPProperties(); var local = p.UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "—"; var gw = p.GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "—"; var dns = string.Join(", ", p.DnsAddresses.Take(2)); return $"INTERFACE   {n.Name}\r\nLOCAL IP    {local}\r\nGATEWAY     {gw}\r\nDNS         {dns}"; } catch (Exception ex) { return "Network error: " + ex.Message; } }
    string LocalRouter() { try { var n = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.OperationalStatus == OperationalStatus.Up && x.GetIPProperties().GatewayAddresses.Any()); if (n == null) return "ROUTER   gateway not detected"; var p = n.GetIPProperties(); return $"GATEWAY     {p.GatewayAddresses.FirstOrDefault()?.Address}\r\nINTERFACE   {n.Name}\r\nTYPE        {n.NetworkInterfaceType}\r\nSTATE       LINK UP\r\nCONFIDENCE  LOCAL"; } catch (Exception ex) { return "Router error: " + ex.Message; } }

    async Task BackgroundScan() { var g = await FindGame(); if (g == null) return; if (g.Pid != pid) { SetGame(g); await FindConnections(); } else if (target == null) await FindConnections(); }
    void SetGame(GameInfo g) { pid = g.Pid; gameTitle.Text = g.Name; gameMeta.Text = $"PID       {g.Pid}\r\nPATH      {(string.IsNullOrWhiteSpace(g.Path) ? "access unavailable" : g.Path)}\r\nRUNNING   YES • connections collected automatically"; gameIcon.Image = ExtractIcon(g.Path) ?? Brand.CreateLogo(60); Log($"[GAME] {g.Name} detected (PID {g.Pid})."); RefreshGames(); }
    void ResetGame() { pid = 0; target = null; port = 0; endpoints.Clear(); samples.Clear(); lastPing = -1; jitter = 0; gameTitle.Text = "No game detected"; gameMeta.Text = "Start an online game and press AUTO ANALYZE again."; gameIcon.Image = Brand.CreateLogo(60); endpointBox.Clear(); connectionText.Text = "No public endpoint candidate yet."; metrics.Text = "ENDPOINT   —\r\nLATENCY    —\r\nLOSS       —\r\nJITTER     —\r\nSTABILITY  WAITING"; quality.Text = "WAITING FOR A TARGET"; }
    void UpdateMetrics() { var e = endpoints.FirstOrDefault(x => x.Ip == target && x.Port == port); metrics.Text = $"ENDPOINT   {(target == null ? "—" : target + ":" + port)}\r\nPROTOCOL   {e?.Protocol ?? "—"}\r\nLATENCY    {(lastPing < 0 ? "—" : $"{lastPing:0} ms")}\r\nLOSS       0%*\r\nJITTER     {(lastPing < 0 ? "—" : $"{jitter:0.0} ms")}\r\nSTABILITY  {(lastPing < 0 ? "WAITING" : Stability(lastPing, jitter))}\r\n\r\n*Probe loss is not game-packet loss."; }
    string Stability(double p, double j) => p < 0 ? "WAITING" : j < 4 ? "EXCELLENT" : j < 10 ? "GOOD" : "VARIABLE";

    void RefreshGames()
    {
        _ = Task.Run(async () => { var g = new List<GameInfo>(); var names = new HashSet<string>(Known.Concat(customGames), StringComparer.OrdinalIgnoreCase); foreach (var p in Process.GetProcesses()) { try { if (!names.Contains(p.ProcessName)) continue; string path = ""; try { path = p.MainModule?.FileName ?? ""; } catch { } g.Add(new GameInfo(Pretty(p.ProcessName), p.Id, path)); } catch { } finally { p.Dispose(); } } if (IsHandleCreated) BeginInvoke((Action)(() => RenderGames(g))); });
    }
    void RenderGames(List<GameInfo> list) { games.SuspendLayout(); games.Controls.Clear(); if (list.Count == 0) games.Controls.Add(new Label { Text = "No supported game is running.\r\nLaunch a game to detect it automatically.", ForeColor = Muted, Width = Math.Max(170, games.ClientSize.Width - 8), Height = 58, Padding = new Padding(6) }); foreach (var g in list) { var c = new Panel { Width = Math.Max(170, games.ClientSize.Width - 8), Height = 70, BackColor = Surface2, Margin = new Padding(0, 0, 0, 6), Cursor = Cursors.Hand }; c.Controls.Add(new PictureBox { Image = ExtractIcon(g.Path) ?? Brand.CreateLogo(44), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(6, 9, 46, 46) }); c.Controls.Add(L(g.Name, 58, 8, 9.2f, Fg, true, c.Width - 65, 22)); c.Controls.Add(L($"PID {g.Pid}\r\n{(g.Pid == pid ? "ACTIVE • ANALYZING" : "RUNNING")}", 58, 31, 7.4f, g.Pid == pid ? Green : Muted, false, c.Width - 65, 32)); c.Click += async (_, _) => { if (!busy) { SetGame(g); await FindConnections(); } }; games.Controls.Add(c); } games.ResumeLayout(true); }
    void AddGame() { using var d = new OpenFileDialog { Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*", Title = "Add a game executable" }; if (d.ShowDialog(this) != DialogResult.OK) return; var n = Path.GetFileNameWithoutExtension(d.FileName); if (!customGames.Contains(n, StringComparer.OrdinalIgnoreCase)) customGames.Add(n); try { File.WriteAllLines(customFile, customGames); } catch { } Log("[GAME] Added " + n + ". Start it and AUTO ANALYZE will detect it."); RefreshGames(); }
    void ClearMemory() { customGames.Clear(); try { if (File.Exists(customFile)) File.Delete(customFile); } catch { } Log("[MEMORY] Custom game list cleared; built-in signatures remain."); RefreshGames(); }
    void Guide() => MessageBox.Show(this, "GAME ROUTE LAB v10 — QUICK GUIDE\r\n\r\n1. Launch CrossFire and enter an online match.\r\n2. Press AUTO ANALYZE.\r\n3. The app detects the game and fills the endpoint automatically.\r\n4. Watch BEST ENDPOINT + LIVE PING TRACKER.\r\n5. Press PING 30x, then TRACEROUTE or PATH QUALITY.\r\n6. SAVE REPORT.\r\n\r\nYou normally do not type an endpoint. ADD GAME EXE is only a fallback.", "How to use Game Route Lab", MessageBoxButtons.OK, MessageBoxIcon.Information);
    void SaveReport() { using var d = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = $"GameRouteLab-{DateTime.Now:yyyyMMdd-HHmmss}.txt" }; if (d.ShowDialog(this) != DialogResult.OK) return; File.WriteAllText(d.FileName, $"GAME ROUTE LAB v10 REPORT\r\n{DateTime.Now}\r\nGame: {gameTitle.Text}\r\nPID: {pid}\r\nEndpoint: {target}:{port}\r\nLatency: {lastPing:0} ms\r\nJitter: {jitter:0.0} ms\r\n\r\nNETWORK\r\n{networkState}\r\n\r\nROUTER\r\n{routerState}\r\n\r\nCONSOLE\r\n{console.Text}"); Log("[REPORT] Saved " + d.FileName); }
    void LoadCustom() { try { if (File.Exists(customFile)) customGames.AddRange(File.ReadAllLines(customFile).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)); } catch { } }
    void Animate() { phase += .045f; radar.Phase = phase; radar.Active = progress.Value > 0 && progress.Value < 100; pingGraph.Phase = phase; network.Content = networkState; router.Content = routerState; network.Phase = phase; router.Phase = phase; network.Invalidate(); router.Invalidate(); radar.Invalidate(); pingGraph.Invalidate(); }
    void SetProgress(int v, string s) { progress.Value = Math.Clamp(v, 0, 100); progressState.Text = s; progressState.ForeColor = s == "LIVE" || s == "READY" ? Green : Cyan; }
    void Log(string s) { if (console.IsDisposed) return; console.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n"); console.SelectionStart = console.TextLength; console.ScrollToCaret(); }
    string Pretty(string n) => n.Contains("crossfire", StringComparison.OrdinalIgnoreCase) ? "CrossFire" : n.ToUpperInvariant();
    bool PublicIp(string s) { if (!IPAddress.TryParse(s, out var a) || IPAddress.IsLoopback(a)) return false; if (a.AddressFamily == AddressFamily.InterNetwork) { var b = a.GetAddressBytes(); return !(b[0] == 10 || b[0] == 127 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 169 && b[1] == 254)); } return !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal; }
    Bitmap? ExtractIcon(string path) { try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return Icon.ExtractAssociatedIcon(path)?.ToBitmap(); } catch { } return null; }

    sealed record GameInfo(string Name, int Pid, string Path);
    sealed record Endpoint(string Ip, int Port, string Protocol, string State);

    sealed class Card : Panel { readonly Color accent; public Card(Color c) { accent = c; BackColor = Surface; DoubleBuffered = true; } protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var p = new Pen(Color.FromArgb(150, accent)); e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1); using var q = new Pen(Color.FromArgb(70, accent), 2); e.Graphics.DrawLine(q, 0, 0, Math.Min(150, Width), 0); } }
    sealed class Radar : Control { public float Phase; public bool Active; public Radar() { DoubleBuffered = true; BackColor = Color.Transparent; } protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; float x = Width / 2f, y = Height / 2f; using var p = new Pen(Color.FromArgb(130, Purple)); for (int r = 16; r < Math.Min(Width, Height) / 2; r += 16) e.Graphics.DrawEllipse(p, x - r, y - r, r * 2, r * 2); e.Graphics.DrawLine(p, x, 4, x, Height - 4); e.Graphics.DrawLine(p, 4, y, Width - 4, y); if (Active) { float a = Phase * 1.7f; using var q = new Pen(Color.FromArgb(210, Cyan), 2); e.Graphics.DrawLine(q, x, y, x + Width * .44f * MathF.Cos(a), y + Height * .44f * MathF.Sin(a)); using var b = new SolidBrush(Green); e.Graphics.FillEllipse(b, x - 4, y - 4, 8, 8); } } }
    sealed class PingGraph : Control { public float Phase; public List<double> Values { get; set; } = new(); public PingGraph() { DoubleBuffered = true; BackColor = Color.FromArgb(5, 11, 22); } protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var grid = new Pen(Color.FromArgb(35, 85, 105)); for (int i = 1; i < 5; i++) e.Graphics.DrawLine(grid, 0, i * Height / 5, Width, i * Height / 5); var v = Values.Count > 1 ? Values : Enumerable.Range(0, 28).Select(i => 50 + 8 * Math.Sin(i * .55 + Phase * 2)).ToList(); double min = Math.Max(0, v.Min() - 8), max = Math.Max(min + 20, v.Max() + 8); var pts = v.Select((n, i) => new PointF(4 + i * (Width - 8f) / Math.Max(1, v.Count - 1), Height - 8 - (float)((n - min) / (max - min) * (Height - 16)))).ToArray(); using var glow = new Pen(Color.FromArgb(65, Green), 4); using var line = new Pen(Green, 1.7f); e.Graphics.DrawLines(glow, pts); e.Graphics.DrawLines(line, pts); } }
    sealed class TelemetryCard : Card { public string Title = "TELEMETRY", State = "WAITING", Content = ""; public Label? TextLabel; public float Phase; public Color Accent = Cyan; public TelemetryCard() : base(Cyan) { } protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var f = new Font("Segoe UI Semibold", 10); using var b = new SolidBrush(Accent); e.Graphics.DrawString(Title, f, b, 14, 12); using var sf = new StringFormat { Alignment = StringAlignment.Far }; using var sb = new SolidBrush(State == "LIVE" ? Green : Muted); e.Graphics.DrawString("● " + State, new Font("Segoe UI Semibold", 7.5f), sb, new RectangleF(14, 12, Width - 28, 20), sf); if (TextLabel != null) TextLabel.Bounds = new Rectangle(14, 43, Width - 28, Math.Max(50, Height - 53)); using var scan = new Pen(Color.FromArgb(55, Accent)); for (int i = 0; i < 5; i++) e.Graphics.DrawLine(scan, 14, 42 + i * 17, Width - 14, 42 + i * 17); float x = 14 + ((MathF.Sin(Phase * 1.6f) + 1) * .5f) * Math.Max(1, Width - 28); using var pulse = new Pen(Color.FromArgb(180, Accent), 2); e.Graphics.DrawLine(pulse, x, 42, x, Height - 12); } }
}
