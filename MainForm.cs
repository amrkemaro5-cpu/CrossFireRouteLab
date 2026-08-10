using System.Diagnostics;
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
    readonly RichTextBox log = new();
    readonly List<Button> buttons = new();
    bool busy;

    public MainForm()
    {
        Text = "Game Route Lab v2.0";
        Width = 1280; Height = 820; MinimumSize = new Size(1050, 680);
        StartPosition = FormStartPosition.CenterScreen;

        var head = new Panel { Dock = DockStyle.Top, Height = 82, Padding = new Padding(15) };
        head.Controls.Add(new Label { Text = "GAME ROUTE LAB", AutoSize = true, Font = new Font("Segoe UI", 20, FontStyle.Bold), Location = new Point(15, 8) });
        head.Controls.Add(new Label { Text = "Automatic game + router detection • local analysis engine • READ-ONLY", AutoSize = true, ForeColor = Color.DarkCyan, Location = new Point(17, 47) });
        Controls.Add(head);

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(12), AutoScroll = true, WrapContents = true };
        bar.Controls.Add(new Label { Text = "Optional endpoint:", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
        target.Width = 220; target.PlaceholderText = "IP or hostname"; bar.Controls.Add(target);
        AddButton(bar, "AUTO ANALYZE GAME", AutoAnalyze);
        AddButton(bar, "Detect Router", DetectRouter);
        AddButton(bar, "Detect Games", DetectGames);
        AddButton(bar, "Find Connections", Connections);
        AddButton(bar, "Network Snapshot", Snapshot);
        AddButton(bar, "DNS Discovery", DnsDiscovery);
        AddButton(bar, "Route Table", Routes);
        AddButton(bar, "Ping 30x", Ping);
        AddButton(bar, "Traceroute", Trace);
        AddButton(bar, "Path Quality", Path);
        AddButton(bar, "Multi Scan", Multi);
        AddButton(bar, "Save Report", SaveReport);
        Controls.Add(bar);

        var sp = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 5, 12, 5) };
        status.Text = "READY • READ-ONLY"; status.AutoSize = true; status.ForeColor = Color.DarkGreen; sp.Controls.Add(status); Controls.Add(sp);
        log.Dock = DockStyle.Fill; log.ReadOnly = true; log.WordWrap = false; log.BackColor = Color.FromArgb(18, 22, 27); log.ForeColor = Color.FromArgb(225, 232, 240); log.Font = new Font("Consolas", 10); Controls.Add(log);

        L("============================================================");
        L("GAME ROUTE LAB v2.0");
        L("============================================================");
        L("Automatic game discovery + router fingerprint + path analysis.");
        L("No IP copying is required. The analyzer reads active connections itself.");
        L("It detects the local gateway and fingerprints the router web interface when available.");
        L("IMPORTANT: this build does NOT change routes, DNS, PPPoE, router settings, firmware, or use a VPN.");
        L("");
    }

    void AddButton(Control c, string text, Func<Task> action)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 34, Margin = new Padding(4) };
        b.Click += async (_, _) => await Safe(action); buttons.Add(b); c.Controls.Add(b);
    }

    void L(string s) { if (InvokeRequired) { BeginInvoke(() => L(s)); return; } log.AppendText(s + Environment.NewLine); log.SelectionStart = log.TextLength; log.ScrollToCaret(); }

    async Task Safe(Func<Task> f)
    {
        if (busy) return; busy = true; foreach (var b in buttons) b.Enabled = false; status.Text = "ANALYZING..."; status.ForeColor = Color.DarkOrange;
        try { await f(); } catch (Exception e) { L("[ERROR] " + e.Message); } finally { busy = false; foreach (var b in buttons) b.Enabled = true; status.Text = "READY • READ-ONLY"; status.ForeColor = Color.DarkGreen; }
    }

    async Task<string> Cmd(string file, string args, int timeout = 90000)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 }) ?? throw new Exception("Could not start " + file);
        var o = await p.StandardOutput.ReadToEndAsync(); var e = await p.StandardError.ReadToEndAsync();
        using var c = new CancellationTokenSource(timeout);
        try { await p.WaitForExitAsync(c.Token); } catch { try { p.Kill(true); } catch { } return o + "\nTIMEOUT\n" + e; }
        return o + (string.IsNullOrWhiteSpace(e) ? "" : "\n" + e);
    }

    string Target() { var t = target.Text.Trim(); if (string.IsNullOrEmpty(t)) throw new Exception("No endpoint is selected. Use AUTO ANALYZE or enter one manually."); return t; }

    async Task DetectRouter()
    {
        L("\n=== AUTOMATIC ROUTER FINGERPRINT ===");
        var r = await RouterDetector.DetectAsync();
        L($"Gateway:        {r.Gateway}");
        L($"Vendor:         {r.Vendor}");
        L($"Model:          {r.Model}");
        L($"Firmware:       {r.Firmware}");
        L($"Management UI:  {r.ManagementUrl}");
        L($"Confidence:     {r.Confidence}");
        L($"Notes:          {r.Notes}");
    }

    async Task DetectGames()
    {
        L("\n=== AUTOMATIC GAME DISCOVERY ===");
        var foreground = GameScanner.GetForegroundProcessName();
        if (!string.IsNullOrWhiteSpace(foreground)) L("Current foreground process: " + foreground);
        var items = await GameScanner.DiscoverAsync();
        if (items.Count == 0) { L("No public active game-like connections found. Start a game and enter online play."); return; }
        foreach (var x in items.Take(40)) L($"{(x.LikelyGame ? "GAME?" : "NET")}  {x.ProcessName} PID={x.Pid} {x.Protocol} {x.RemoteIp}:{x.RemotePort} {x.State}");
        L($"\nDiscovered {items.Count} public active endpoints. No IP copying is required by the analyzer.");
    }

    async Task AutoAnalyze()
    {
        L("\n============================================================");
        L("AUTOMATIC GAME ROUTE ANALYSIS");
        L("============================================================");
        L("Step 1/4: identifying your router and firmware...");
        var router = await RouterDetector.DetectAsync();
        L($"Router: {router.Vendor} {router.Model}");
        L($"Firmware: {router.Firmware}");
        L($"Gateway: {router.Gateway}");
        L($"Detection confidence: {router.Confidence}");

        L("\nStep 2/4: discovering active game/network endpoints...");
        var endpoints = await GameScanner.DiscoverAsync();
        var candidates = endpoints.Where(x => x.LikelyGame).Take(10).ToList();
        if (candidates.Count == 0) candidates = endpoints.Take(10).ToList();
        if (candidates.Count == 0) { L("No public candidates found. Start an online game and run AUTO ANALYZE again."); return; }
        L($"Candidates selected automatically: {candidates.Count}");
        foreach (var c in candidates) L($"  {c.ProcessName} PID {c.Pid} -> {c.RemoteIp}:{c.RemotePort} ({c.Protocol})");

        L("\nStep 3/4: measuring candidates. ICMP is optional because many game servers block ping.");
        var results = new List<CandidateScore>();
        foreach (var c in candidates)
        {
            var ping = await PingQuick(c.RemoteIp);
            var tcp = c.RemotePort > 0 ? await TcpQuick(c.RemoteIp, c.RemotePort) : null;
            var trace = await Cmd("tracert.exe", $"-d -h 20 -w 600 {c.RemoteIp}", 30000);
            var hops = CountTraceHops(trace);
            var score = Score(c, ping, tcp, hops);
            results.Add(new CandidateScore(c, ping, tcp, hops, score));
            L($"\n[{c.RemoteIp}:{c.RemotePort}] {c.ProcessName}");
            L($"  Game-like connection: {c.LikelyGame}");
            L($"  ICMP: {ping.Description}");
            L($"  TCP connect: {(tcp.HasValue ? tcp.Value.Description : "N/A")}");
            L($"  Traceroute responding hops: {hops}");
            L($"  Analyzer score: {score:0}/100");
        }

        L("\nStep 4/4: ranking endpoints...");
        foreach (var r in results.OrderByDescending(x => x.Score)) L($"  {r.Score,3:0}/100  {r.Endpoint.RemoteIp}:{r.Endpoint.RemotePort}  {r.Endpoint.ProcessName}");
        var best = results.OrderByDescending(x => x.Score).First();
        target.Text = best.Endpoint.RemoteIp;
        L("\n============================================================");
        L($"BEST VERIFIED CURRENT CANDIDATE: {best.Endpoint.RemoteIp}:{best.Endpoint.RemotePort}");
        L($"Process: {best.Endpoint.ProcessName} (PID {best.Endpoint.Pid})");
        L($"Score: {best.Score:0}/100");
        L(Explain(best));
        L("============================================================");
        L("Note: a best candidate is not a guaranteed better game ping. The analyzer measures evidence and does not treat ICMP-blocked game servers as 100% bad.");
    }

    sealed record Probe(double AvgMs, double MinMs, double MaxMs, int Received, int Sent, string Description);
    sealed record TcpProbe(double AvgMs, int Successes, int Attempts, string Description);
    sealed record CandidateScore(GameEndpoint Endpoint, Probe Ping, TcpProbe? Tcp, int Hops, double Score);

    async Task<Probe> PingQuick(string ip)
    {
        using var ping = new Ping(); var values = new List<long>(); int sent = 6, received = 0;
        for (var i = 0; i < sent; i++)
        {
            try { var r = await ping.SendPingAsync(ip, 1000); if (r.Status == IPStatus.Success) { received++; values.Add(r.RoundtripTime); } } catch { }
        }
        if (received == 0) return new Probe(0, 0, 0, 0, sent, "BLOCKED/NO ICMP RESPONSE (not proof of game loss)");
        return new Probe(values.Average(), values.Min(), values.Max(), received, sent, $"{values.Average():0.0} ms avg, {sent - received}/{sent} lost");
    }

    async Task<TcpProbe> TcpQuick(string ip, int port)
    {
        var values = new List<double>(); int success = 0, attempts = 3;
        for (var i = 0; i < attempts; i++)
        {
            try { using var c = new TcpClient(); var sw = Stopwatch.StartNew(); var task = c.ConnectAsync(ip, port); var done = await Task.WhenAny(task, Task.Delay(1500)); if (done == task && c.Connected) { sw.Stop(); success++; values.Add(sw.Elapsed.TotalMilliseconds); } } catch { }
        }
        return success == 0 ? new TcpProbe(0, 0, attempts, "NO NEW TCP CONNECT (existing game connection may still be valid)") : new TcpProbe(values.Average(), success, attempts, $"{values.Average():0.0} ms avg, {success}/{attempts} connects");
    }

    static int CountTraceHops(string text) => text.Split('\n').Count(line => Regex.IsMatch(line.TrimStart(), @"^\d+\s+"));

    static double Score(GameEndpoint e, Probe p, TcpProbe? t, int hops)
    {
        var s = 25.0;
        if (e.LikelyGame) s += 30;
        if (e.Protocol == "TCP" && e.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) s += 20;
        if (p.Received > 0) s += Math.Max(0, 15 - Math.Min(15, p.AvgMs / 10));
        else if (t is { Successes: > 0 }) s += 12;
        if (t is { Successes: > 0 }) s += 8;
        if (hops > 0 && hops <= 16) s += 2;
        return Math.Clamp(s, 0, 100);
    }

    static string Explain(CandidateScore r)
    {
        var lines = new StringBuilder();
        lines.AppendLine("Why it ranked first:");
        if (r.Endpoint.LikelyGame) lines.AppendLine("• Process/connection looks game-related.");
        if (r.Endpoint.Protocol == "TCP" && r.Endpoint.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) lines.AppendLine("• The game process has an established connection to this endpoint.");
        if (r.Ping.Received == 0) lines.AppendLine("• ICMP is blocked or ignored; this was NOT scored as 100% game packet loss.");
        else lines.AppendLine($"• ICMP baseline: {r.Ping.AvgMs:0.0} ms average, {r.Ping.Sent - r.Ping.Received}/{r.Ping.Sent} loss.");
        if (r.Tcp is { Successes: > 0 }) lines.AppendLine($"• TCP connection test succeeded at {r.Tcp.AvgMs:0.0} ms average.");
        lines.AppendLine($"• Traceroute returned {r.Hops} responding hop lines.");
        return lines.ToString().TrimEnd();
    }

    async Task Detect() { await DetectGames(); }

    async Task Connections()
    {
        L("\n=== LIVE CONNECTION DISCOVERY ===");
        var items = await GameScanner.DiscoverAsync();
        if (items.Count == 0) { L("No public endpoint found. Start an actual online game."); return; }
        foreach (var x in items.Take(60)) L($"{(x.LikelyGame ? "GAME?" : "NET")}  {x.ProcessName} PID={x.Pid} {x.Protocol} {x.RemoteIp}:{x.RemotePort} {x.State}");
        var first = items.FirstOrDefault(x => x.LikelyGame) ?? items.First();
        target.Text = first.RemoteIp;
        L($"\nAutomatically selected candidate: {first.RemoteIp}:{first.RemotePort}");
    }

    async Task Snapshot() { L("\n=== NETWORK / PPPoE SNAPSHOT ==="); L("Time: " + DateTime.Now); L("\n--- IPCONFIG /ALL ---"); L(await Cmd("ipconfig.exe", "/all", 30000)); L("\n--- ROUTE PRINT ---"); L(await Cmd("route.exe", "print", 30000)); L("\n--- INTERFACES ---"); L(await Cmd("netsh.exe", "interface ipv4 show interfaces", 30000)); L("\nNo network configuration was changed."); }
    async Task DnsDiscovery() { L("\n=== CURRENT DNS DISCOVERY ==="); foreach (var h in new[] { "crossfire.z8games.com", "z8games.com", "cfpatch.z8games.com" }) { try { var a = await Dns.GetHostAddressesAsync(h); L($"{h}: {string.Join(", ", a.Where(x => x.AddressFamily == AddressFamily.InterNetwork))}"); } catch { L(h + ": resolution failed"); } } }
    async Task Routes() { L("\n=== WINDOWS ROUTE TABLE ==="); L(await Cmd("route.exe", "print", 30000)); }
    async Task Ping() { var t = Target(); L($"\n=== PING 30x {t} ==="); L(await Cmd("ping.exe", $"-n 30 -w 1000 {t}", 60000)); }
    async Task Trace() { var t = Target(); L($"\n=== TRACEROUTE {t} ==="); L(await Cmd("tracert.exe", $"-d -h 30 -w 800 {t}", 90000)); }
    async Task Path() { var t = Target(); L($"\n=== PATH QUALITY {t} ==="); L("This can take several minutes."); L(await Cmd("pathping.exe", $"-n -q 20 -w 500 {t}", 300000)); }
    async Task Multi() { var t = Target(); string[] ips; try { ips = (await Dns.GetHostAddressesAsync(t)).Where(x => x.AddressFamily == AddressFamily.InterNetwork).Select(x => x.ToString()).Distinct().Take(8).ToArray(); } catch { ips = new[] { t }; } L("\n=== MULTI-ENDPOINT QUICK SCAN ==="); foreach (var ip in ips) { L("\n--- " + ip + " ---"); L(await Cmd("ping.exe", $"-n 12 -w 800 {ip}", 35000)); } }
    async Task SaveReport() { using var d = new SaveFileDialog { Filter = "Text report (*.txt)|*.txt|All files (*.*)|*.*", FileName = $"Game_Route_Lab_{DateTime.Now:yyyyMMdd_HHmmss}.txt" }; if (d.ShowDialog(this) != DialogResult.OK) return; await File.WriteAllTextAsync(d.FileName, log.Text, Encoding.UTF8); MessageBox.Show(this, "Report saved.", "Game Route Lab", MessageBoxButtons.OK, MessageBoxIcon.Information); }
}
