using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed class MainForm : Form
{
    readonly TextBox target = new();
    readonly Label status = new();
    readonly Label networkLabel = new();
    readonly RichTextBox log = new();
    readonly ListView games = new();
    readonly ImageList gameImages = new();
    readonly List<Button> buttons = new();
    readonly List<GameProfile> memory = new();
    bool busy;

    public MainForm()
    {
        Text = "Game Route Lab";
        Width = 1400; Height = 900; MinimumSize = new Size(1120, 720);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(242, 245, 248);

        var header = new Panel { Dock = DockStyle.Top, Height = 88, Padding = new Padding(16), BackColor = Color.White };
        header.Controls.Add(new Label { Text = "GAME ROUTE LAB", AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold), Location = new Point(16, 7) });
        header.Controls.Add(new Label { Text = "Automatic game • ISP • router • endpoint • path analysis  |  READ-ONLY", AutoSize = true, ForeColor = Color.Teal, Location = new Point(18, 50) });
        networkLabel.AutoSize = true; networkLabel.ForeColor = Color.DimGray; networkLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right; networkLabel.Location = new Point(760, 20); networkLabel.Text = "Network: not scanned"; header.Controls.Add(networkLabel);
        Controls.Add(header);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 122, Padding = new Padding(12), BackColor = Color.White, WrapContents = true, AutoScroll = true };
        target.Width = 220; target.PlaceholderText = "Optional IP / hostname";
        top.Controls.Add(new Label { Text = "Manual endpoint:", AutoSize = true, Padding = new Padding(0, 9, 4, 0) }); top.Controls.Add(target);
        AddButton(top, "AUTO ANALYZE GAME", AutoAnalyze);
        AddButton(top, "Refresh Games", DetectGames);
        AddButton(top, "Detect Network", DetectNetwork);
        AddButton(top, "Detect Router", DetectRouter);
        AddButton(top, "Find Connections", Connections);
        AddButton(top, "Route Table", Routes);
        AddButton(top, "Ping 30x", Ping);
        AddButton(top, "Traceroute", Trace);
        AddButton(top, "Path Quality", Path);
        AddButton(top, "Save Report", SaveReport);
        Controls.Add(top);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 310, FixedPanel = FixedPanel.Panel1, BackColor = Color.FromArgb(225, 231, 237) };
        split.Panel1.Padding = new Padding(10);
        var leftTitle = new Label { Text = "GAME MEMORY", Dock = DockStyle.Top, Height = 32, Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Color.FromArgb(30, 45, 60) };
        split.Panel1.Controls.Add(leftTitle);
        gameImages.ColorDepth = ColorDepth.Depth32Bit; gameImages.ImageSize = new Size(64, 64);
        games.Dock = DockStyle.Fill; games.View = View.LargeIcon; games.LargeImageList = gameImages; games.MultiSelect = false; games.HideSelection = false; games.BackColor = Color.FromArgb(248, 250, 252); games.BorderStyle = BorderStyle.None;
        games.SelectedIndexChanged += (_, _) => ShowSelectedMemory();
        split.Panel1.Controls.Add(games);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        status.Text = "READY • READ-ONLY"; status.AutoSize = true; status.ForeColor = Color.DarkGreen; status.Dock = DockStyle.Top; status.Height = 30; right.Controls.Add(status);
        log.Dock = DockStyle.Fill; log.ReadOnly = true; log.WordWrap = false; log.BackColor = Color.FromArgb(18, 22, 27); log.ForeColor = Color.FromArgb(225, 232, 240); log.Font = new Font("Consolas", 10); right.Controls.Add(log);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);

        memory.AddRange(GameProfileStore.Load());
        RefreshMemoryList();
        L("============================================================");
        L("GAME ROUTE LAB v3.0");
        L("============================================================");
        L("No IP copying is required. The analyzer discovers the active game's process and connections itself.");
        L("Local game memory is stored under %LOCALAPPDATA%\\GameRouteLab. It contains no router password.");
        L("The analyzer never changes Windows routes, DNS, PPPoE, router settings, firmware, or uses a VPN.");
        L("");
    }

    void AddButton(Control c, string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 34, Margin = new Padding(4) };
        b.Click += async (_, _) => await Safe(action); buttons.Add(b); c.Controls.Add(b);
    }

    void L(string s)
    {
        if (InvokeRequired) { BeginInvoke(() => L(s)); return; }
        log.AppendText(s + Environment.NewLine); log.SelectionStart = log.TextLength; log.ScrollToCaret();
    }

    async Task Safe(Func<Task> f)
    {
        if (busy) return; busy = true; foreach (var b in buttons) b.Enabled = false; status.Text = "ANALYZING..."; status.ForeColor = Color.DarkOrange;
        try { await f(); } catch (Exception e) { L("[ERROR] " + e.Message); } finally { busy = false; foreach (var b in buttons) b.Enabled = true; status.Text = "READY • READ-ONLY"; status.ForeColor = Color.DarkGreen; }
    }

    async Task<string> Cmd(string file, string args, int timeout = 60000)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 }) ?? throw new Exception("Could not start " + file);
        var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
        using var c = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(c.Token); } catch { try { p.Kill(true); } catch { } return (await o) + "\nTIMEOUT\n" + (await e); }
        var err = await e;
        return (await o) + (string.IsNullOrWhiteSpace(err) ? "" : "\n" + err);
    }

    async Task DetectNetwork()
    {
        L("\n=== AUTOMATIC NETWORK / ISP PROFILE ===");
        var n = await NetworkProfileDetector.DetectAsync();
        networkLabel.Text = $"ISP: {n.ISP}  |  ASN: {n.ASN}  |  Router GW: {n.Gateway}";
        L($"Interface:      {n.InterfaceName}"); L($"Local IP:       {n.LocalIp}"); L($"Gateway:        {n.Gateway}"); L($"WAN type:       {n.WanType}");
        L($"Public IP:      {n.PublicIp}"); L($"ISP:            {n.ISP}"); L($"Organization:   {n.Organization}"); L($"ASN:            {n.ASN}"); L($"Location:       {n.City}, {n.Country}"); L($"DNS:            {n.DnsServers}"); L($"Note:           {n.Notes}");
    }

    async Task DetectRouter()
    {
        L("\n=== AUTOMATIC ROUTER FINGERPRINT ===");
        var r = await RouterDetector.DetectAsync();
        L($"Gateway:        {r.Gateway}"); L($"Vendor:         {r.Vendor}"); L($"Model:          {r.Model}"); L($"Firmware:       {r.Firmware}"); L($"Management UI:  {r.ManagementUrl}"); L($"Confidence:     {r.Confidence}"); L($"Notes:          {r.Notes}");
    }

    async Task DetectGames()
    {
        L("\n=== AUTOMATIC ANY-GAME DISCOVERY ===");
        var items = await GameScanner.DiscoverAsync();
        var groups = items.Where(x => x.LikelyGame).GroupBy(x => new { x.Pid, x.ProcessName, x.ExecutablePath }).ToList();
        if (groups.Count == 0) { L("No high-confidence game process found. Start an online game and try again."); return; }
        foreach (var g in groups)
        {
            var p = GameProfileStore.Touch(g.Key.ProcessName, g.Key.ExecutablePath);
            UpsertMemory(p);
            L($"GAME  {g.Key.ProcessName}  PID={g.Key.Pid}  connections={g.Count()}  confidence={g.Max(x => x.Confidence)}%");
        }
        RefreshMemoryList();
        L($"Discovered {groups.Count} game process(es). Icons are extracted from the game's executable and cached as PNG files.");
    }

    async Task AutoAnalyze()
    {
        L("\n============================================================"); L("AUTO GAME ROUTE ANALYSIS"); L("============================================================");
        L("1/5  Detecting router, firmware and local network...");
        var router = await RouterDetector.DetectAsync();
        var network = await NetworkProfileDetector.DetectAsync();
        networkLabel.Text = $"ISP: {network.ISP}  |  ASN: {network.ASN}  |  Router: {router.Model}";
        L($"Router: {router.Vendor} {router.Model} | Firmware: {router.Firmware} | Gateway: {router.Gateway}");
        L($"ISP: {network.ISP} | Org: {network.Organization} | ASN: {network.ASN} | Public IP: {network.PublicIp}");

        L("\n2/5  Detecting the active game automatically...");
        var endpoints = await GameScanner.DiscoverAsync();
        if (endpoints.Count == 0) { L("No public active connections found. Start an online game first."); return; }
        var foregroundPid = GameScanner.GetForegroundPid();
        var gameEndpoints = endpoints.Where(x => x.LikelyGame).ToList();
        if (gameEndpoints.Count == 0) gameEndpoints = endpoints.Take(12).ToList();
        var selected = gameEndpoints.Where(x => x.Pid == foregroundPid).ToList();
        if (selected.Count == 0) selected = gameEndpoints.GroupBy(x => x.Pid).OrderByDescending(g => g.Max(x => x.Confidence)).First().ToList();
        var game = selected.First();
        var profile = GameProfileStore.Touch(game.ProcessName, game.ExecutablePath); UpsertMemory(profile); RefreshMemoryList();
        L($"Selected game: {game.ProcessName} (PID {game.Pid})");
        L($"Memory: {profile.Observations} previous analyses | Last best: {profile.LastBestEndpoint}");
        L($"Connections discovered for this game: {selected.Count}");
        foreach (var x in selected) L($"  {x.Protocol} {x.RemoteIp}:{x.RemotePort} | confidence={x.Confidence}% | {x.State}");

        L("\n3/5  Testing every discovered game endpoint automatically...");
        var results = new List<CandidateResult>();
        foreach (var e in selected.Take(12))
        {
            var ping = await PingQuick(e.RemoteIp);
            var tcp = e.Protocol == "TCP" && e.RemotePort > 0 ? await TcpQuick(e.RemoteIp, e.RemotePort) : new TcpProbe(0, 0, 0, "UDP endpoint / no TCP probe");
            var traceText = await Cmd("tracert.exe", $"-d -h 20 -w 600 {e.RemoteIp}", 30000);
            var trace = ParseTrace(traceText);
            var score = Score(e, ping, tcp, trace);
            results.Add(new CandidateResult(e, ping, tcp, trace, score));
            L($"\n[{e.RemoteIp}:{e.RemotePort}] {e.Protocol}");
            L($"  ICMP: {ping.Description}");
            L($"  TCP:  {tcp.Description}");
            L($"  Route: {trace.Hops} responding hops; destination/last RTT {trace.LastRttMs:0.0} ms");
            L($"  Score: {score:0}/100");
        }

        L("\n4/5  Comparing endpoints and route evidence...");
        foreach (var r in results.OrderByDescending(x => x.Score)) L($"  {r.Score,3:0}/100  {r.Endpoint.RemoteIp}:{r.Endpoint.RemotePort}  {r.Endpoint.Protocol}");
        var best = results.OrderByDescending(x => x.Score).First();
        target.Text = best.Endpoint.RemoteIp;
        var pathSignature = best.Trace.Signature;
        GameProfileStore.Record(profile, $"{best.Endpoint.RemoteIp}:{best.Endpoint.RemotePort}", best.Score, pathSignature);
        L("\n5/5  Updating this game's local memory...");
        L($"Best current candidate: {best.Endpoint.RemoteIp}:{best.Endpoint.RemotePort}");
        L($"Score: {best.Score:0}/100 | Confidence: {ConfidenceText(best)}");
        L(Explain(best));
        L("\nNo route, DNS, router, firmware, or VPN settings were changed.");
        RefreshMemoryList();
    }

    sealed record Probe(double AvgMs, double MinMs, double MaxMs, int Received, int Sent, string Description);
    sealed record TcpProbe(double AvgMs, int Successes, int Attempts, string Description);
    sealed record TraceProbe(int Hops, double LastRttMs, string Signature, string Raw);
    sealed record CandidateResult(GameEndpoint Endpoint, Probe Ping, TcpProbe Tcp, TraceProbe Trace, double Score);

    async Task<Probe> PingQuick(string ip)
    {
        using var p = new Ping(); var v = new List<long>(); const int sent = 6;
        for (var i = 0; i < sent; i++) { try { var r = await p.SendPingAsync(ip, 900); if (r.Status == IPStatus.Success) v.Add(r.RoundtripTime); } catch { } }
        if (v.Count == 0) return new Probe(0, 0, 0, 0, sent, "BLOCKED / NO ICMP RESPONSE (not proof of game loss)");
        return new Probe(v.Average(), v.Min(), v.Max(), v.Count, sent, $"{v.Average():0.0} ms avg, {sent - v.Count}/{sent} lost");
    }

    async Task<TcpProbe> TcpQuick(string ip, int port)
    {
        var vals = new List<double>(); const int attempts = 3;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                using var c = new TcpClient(); var sw = Stopwatch.StartNew(); var task = c.ConnectAsync(ip, port); var done = await Task.WhenAny(task, Task.Delay(1500));
                if (done == task && c.Connected) { sw.Stop(); vals.Add(sw.Elapsed.TotalMilliseconds); }
            }
            catch { }
        }
        return vals.Count == 0 ? new TcpProbe(0, 0, attempts, "No new TCP connect (existing game socket may still be valid)") : new TcpProbe(vals.Average(), vals.Count, attempts, $"{vals.Average():0.0} ms avg, {vals.Count}/{attempts} connects");
    }

    static TraceProbe ParseTrace(string text)
    {
        var hops = new List<(int No, double Rtt, string Ip)>();
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^(\d+)\s+(.*)$"); if (!m.Success) continue;
            var rest = m.Groups[2].Value;
            var ip = Regex.Matches(rest, @"\b(?:\d{1,3}\.){3}\d{1,3}\b").Cast<Match>().Select(x => x.Value).FirstOrDefault() ?? "*";
            var times = Regex.Matches(rest, @"<?(\d+)\s*ms").Cast<Match>().Select(x => double.Parse(x.Groups[1].Value)).ToList();
            if (ip != "*" || times.Count > 0) hops.Add((int.Parse(m.Groups[1].Value), times.Count == 0 ? 0 : times.Average(), ip));
        }
        var responding = hops.Where(x => x.Ip != "*").ToList();
        var last = responding.LastOrDefault();
        var sig = string.Join(" > ", responding.TakeLast(8).Select(x => x.Ip));
        return new TraceProbe(responding.Count, last.Rtt, sig, text);
    }

    static double Score(GameEndpoint e, Probe p, TcpProbe t, TraceProbe trace)
    {
        var s = 15.0 + (e.Confidence * 0.35);
        if (e.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) s += 10;
        if (p.Received > 0) s += Math.Max(0, 25 - Math.Min(25, p.AvgMs / 5));
        else if (t.Successes > 0) s += 14;
        else if (trace.LastRttMs > 0) s += Math.Max(0, 10 - Math.Min(10, trace.LastRttMs / 20));
        if (t.Successes > 0) s += 10;
        if (trace.Hops > 0) s += 5;
        return Math.Clamp(s, 0, 100);
    }

    static string ConfidenceText(CandidateResult r)
    {
        if (r.Endpoint.Confidence >= 80 && (r.Ping.Received > 0 || r.Tcp.Successes > 0 || r.Trace.Hops > 0)) return "High";
        if (r.Endpoint.Confidence >= 55) return "Medium";
        return "Low";
    }

    static string Explain(CandidateResult r)
    {
        var b = new StringBuilder("Why it ranked first:\n");
        b.AppendLine($"• Game-process confidence: {r.Endpoint.Confidence}%.");
        if (r.Endpoint.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) b.AppendLine("• Existing established connection observed for the game process.");
        if (r.Ping.Received == 0) b.AppendLine("• ICMP was blocked/ignored, so the analyzer did NOT treat it as 100% game packet loss.");
        else b.AppendLine($"• ICMP baseline: {r.Ping.AvgMs:0.0} ms average.");
        if (r.Tcp.Successes > 0) b.AppendLine($"• TCP connect evidence: {r.Tcp.AvgMs:0.0} ms average.");
        if (r.Trace.Hops > 0) b.AppendLine($"• Route evidence: {r.Trace.Hops} responding hops; last responding RTT {r.Trace.LastRttMs:0.0} ms.");
        b.AppendLine("• This is a measured candidate ranking, not a promise that the ISP can be forced onto a different upstream route.");
        return b.ToString().TrimEnd();
    }

    async Task Connections()
    {
        L("\n=== AUTOMATIC LIVE CONNECTION DISCOVERY ===");
        var items = await GameScanner.DiscoverAsync();
        if (items.Count == 0) { L("No public endpoints found. Start an online game."); return; }
        foreach (var x in items.Take(80)) L($"{(x.LikelyGame ? "GAME" : "NET ")}  {x.ProcessName} PID={x.Pid} {x.Protocol} {x.RemoteIp}:{x.RemotePort} {x.State} confidence={x.Confidence}%");
        var first = items.FirstOrDefault(x => x.LikelyGame) ?? items.First(); target.Text = first.RemoteIp;
        L($"Automatically selected: {first.RemoteIp}:{first.RemotePort}. No copying required.");
    }

    async Task Snapshot()
    {
        L("\n=== NETWORK SNAPSHOT ==="); L("Time: " + DateTime.Now); L(await Cmd("ipconfig.exe", "/all", 30000)); L("\n--- ROUTE PRINT ---"); L(await Cmd("route.exe", "print", 30000));
    }

    async Task DnsDiscovery()
    {
        L("\n=== DNS DISCOVERY ==="); foreach (var h in new[] { "crossfire.z8games.com", "z8games.com" }) { try { var a = await Dns.GetHostAddressesAsync(h); L($"{h}: {string.Join(", ", a.Where(x => x.AddressFamily == AddressFamily.InterNetwork))}"); } catch { L(h + ": resolution failed"); } }
    }

    async Task Routes() { L("\n=== WINDOWS ROUTE TABLE ==="); L(await Cmd("route.exe", "print", 30000)); }
    async Task Ping() { var t = Target(); L($"\n=== PING 30x {t} ==="); L(await Cmd("ping.exe", $"-n 30 -w 1000 {t}", 60000)); }
    async Task Trace() { var t = Target(); L($"\n=== TRACEROUTE {t} ==="); L(await Cmd("tracert.exe", $"-d -h 30 -w 800 {t}", 60000)); }
    async Task Path() { var t = Target(); L($"\n=== PATH QUALITY {t} ==="); L("This can take several minutes."); L(await Cmd("pathping.exe", $"-n -q 5 -p 100 -w 800 -h 20 {t}", 180000)); }

    string Target()
    {
        var t = target.Text.Trim(); if (string.IsNullOrWhiteSpace(t)) throw new Exception("No endpoint selected. Use AUTO ANALYZE GAME or enter one manually."); return t;
    }

    void UpsertMemory(GameProfile p)
    {
        var old = memory.FindIndex(x => x.Key == p.Key); if (old >= 0) memory[old] = p; else memory.Add(p);
    }

    void RefreshMemoryList()
    {
        if (InvokeRequired) { BeginInvoke(RefreshMemoryList); return; }
        games.BeginUpdate(); games.Items.Clear(); gameImages.Images.Clear();
        foreach (var p in memory.OrderByDescending(x => x.LastSeenUtc))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(p.IconPath) && File.Exists(p.IconPath)) using (var img = Image.FromFile(p.IconPath)) gameImages.Images.Add(new Bitmap(img));
                else gameImages.Images.Add(CreateFallbackIcon());
            }
            catch { gameImages.Images.Add(CreateFallbackIcon()); }
            var idx = gameImages.Images.Count - 1;
            games.Items.Add(new ListViewItem(p.DisplayName + (p.Observations > 0 ? $"\n{p.Observations} scans" : ""), idx) { Tag = p.Key });
        }
        games.EndUpdate();
    }

    void ShowSelectedMemory()
    {
        if (games.SelectedItems.Count == 0) return;
        var key = games.SelectedItems[0].Tag as string; var p = memory.FirstOrDefault(x => x.Key == key); if (p == null) return;
        L($"\n=== GAME MEMORY: {p.DisplayName} ==="); L($"Executable: {p.ExecutablePath}"); L($"First seen: {p.FirstSeenUtc.ToLocalTime()}"); L($"Last seen:  {p.LastSeenUtc.ToLocalTime()}"); L($"Analyses:   {p.Observations}"); L($"Last best: {p.LastBestEndpoint}  score={p.LastScore:0}/100");
        if (p.RecentPaths.Count > 0) { L("Recent route signatures:"); foreach (var x in p.RecentPaths.Take(5)) L("  " + x); }
    }

    static Bitmap CreateFallbackIcon()
    {
        var b = new Bitmap(64, 64, PixelFormat.Format32bppArgb); using var g = Graphics.FromImage(b); g.Clear(Color.FromArgb(28, 36, 48)); using var pen = new Pen(Color.FromArgb(80, 210, 220), 4); using var brush = new SolidBrush(Color.FromArgb(45, 60, 80));
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.FillEllipse(brush, 8, 17, 48, 30); g.DrawEllipse(pen, 8, 17, 48, 30); g.DrawLine(pen, 19, 32, 29, 32); g.DrawLine(pen, 24, 27, 24, 37); g.FillEllipse(Brushes.White, 41, 28, 5, 5); g.FillEllipse(Brushes.White, 49, 28, 5, 5); return b;
    }

    async Task SaveReport()
    {
        using var dialog = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt", FileName = "GameRouteLab_Report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return; await File.WriteAllTextAsync(dialog.FileName, log.Text); L("\nReport saved: " + dialog.FileName);
    }
}
