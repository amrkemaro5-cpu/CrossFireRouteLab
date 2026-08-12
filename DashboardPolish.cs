using System.Diagnostics;
using System.Net;

namespace CrossFireRouteLab;

public sealed partial class DashboardForm
{
    readonly PictureBox detectedGameIcon = new();
    readonly Timer polishTimer = new() { Interval = 1200 };
    readonly List<double> liveLatency = new();
    readonly List<double> liveJitter = new();
    readonly List<LiveTelemetryOverlay> telemetryOverlays = new();
    bool polishReady;
    bool endpointHooked;
    int liveLost;
    long lastLiveSample = -1;
    DateTime lastEndpointAttempt = DateTime.MinValue;
    string liveTarget = "";

    void EnsurePolishLoaded()
    {
        if (polishReady || IsDisposed || !IsHandleCreated) return;
        polishReady = true;
        var root = Controls.OfType<TableLayoutPanel>().FirstOrDefault();
        if (root == null || root.Controls.Count < 3) return;
        if (root.Controls[2] is not TableLayoutPanel body || body.Controls.Count < 3) return;
        if (body.Controls[1] is not TableLayoutPanel center || center.Controls.Count < 4) return;

        AddDetectedGameIcon(center);
        InstallResponsiveLayout(root, body, center);
        InstallTelemetryCards(body.Controls[2]);
        HookEndpointAutomation();
        polishTimer.Tick += PolishTimer_Tick;
        polishTimer.Start();
        FormClosed += (_, _) => polishTimer.Stop();
        ArrangeDashboard();
    }

    void AddDetectedGameIcon(TableLayoutPanel center)
    {
        if (center.Controls[1] is not GRLCard summary) return;
        detectedGameIcon.Bounds = new Rectangle(20, 52, 58, 58);
        detectedGameIcon.SizeMode = PictureBoxSizeMode.Zoom;
        detectedGameIcon.BackColor = Color.FromArgb(3, 8, 18);
        detectedGameIcon.BorderStyle = BorderStyle.FixedSingle;
        detectedGameIcon.Image = Brand.CreateLogo(56);
        if (!summary.Controls.Contains(detectedGameIcon)) summary.Controls.Add(detectedGameIcon);
        detectedGameIcon.BringToFront();
    }

    void InstallResponsiveLayout(TableLayoutPanel root, TableLayoutPanel body, TableLayoutPanel center)
    {
        root.Resize -= RootPolishResize;
        root.Resize += RootPolishResize;
        body.Resize -= BodyPolishResize;
        body.Resize += BodyPolishResize;
        center.Resize -= CenterPolishResize;
        center.Resize += CenterPolishResize;
    }

    void RootPolishResize(object? sender, EventArgs e) => BeginInvokeSafe(ArrangeDashboard);
    void BodyPolishResize(object? sender, EventArgs e) => BeginInvokeSafe(ArrangeDashboard);
    void CenterPolishResize(object? sender, EventArgs e) => BeginInvokeSafe(ArrangeDashboard);

    void BeginInvokeSafe(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch { }
    }

    void ArrangeDashboard()
    {
        if (IsDisposed) return;
        var root = Controls.OfType<TableLayoutPanel>().FirstOrDefault();
        if (root == null || root.Controls.Count < 3) return;
        if (root.Controls[2] is not TableLayoutPanel body || body.Controls.Count < 3) return;
        if (body.Controls[1] is not TableLayoutPanel center || center.Controls.Count < 4) return;

        body.Padding = new Padding(14, 8, 14, 7);
        var narrow = body.ClientSize.Width < 1200;
        body.ColumnStyles[0].Width = narrow ? 236 : 262;
        body.ColumnStyles[2].Width = narrow ? 292 : 318;
        ArrangeHero(center.Controls[0]);
        ArrangeSummary(center.Controls[1]);
        ArrangeBest(center.Controls[2]);
        ArrangeConsole(center.Controls[3]);
        ArrangeLeft(body.Controls[0]);
        ArrangeRight(body.Controls[2]);
    }

    void ArrangeHero(Control control)
    {
        if (control is not GRLCard card) return;
        var w = card.ClientSize.Width;
        var h = card.ClientSize.Height;
        var radarSize = Math.Min(136, Math.Max(104, h - 50));
        radar.Bounds = new Rectangle(18, Math.Max(18, (h - radarSize) / 2), radarSize, radarSize);
        var left = radar.Right + 22;
        var right = Math.Max(left + 260, w - 24);
        analysisTitle.Bounds = new Rectangle(left, 18, Math.Max(260, right - left), 38);
        var sub = card.Controls.Cast<Control>().FirstOrDefault(c => c is Label l && l.Text.StartsWith("Detecting the game"));
        if (sub != null) sub.Bounds = new Rectangle(left, 58, Math.Max(260, right - left), 24);
        progress.Bounds = new Rectangle(left, 91, Math.Max(220, right - left - 64), 14);
        progressText.Bounds = new Rectangle(right - 54, 84, 54, 24);
        progressText.TextAlign = ContentAlignment.MiddleRight;
        var stepX = left + 6;
        var stepWidth = Math.Max(82, (right - left - 8) / 5);
        var stepLabels = new[] { "DETECT GAME", "FIND CONNECTIONS", "TEST ENDPOINTS", "ANALYZE ROUTES", "GENERATE REPORT" };
        for (var i = 0; i < 5; i++)
        {
            var number = card.Controls.Cast<Control>().FirstOrDefault(c => c is Label l && l.Text == (i + 1).ToString());
            var text = card.Controls.Cast<Control>().FirstOrDefault(c => c is Label l && l.Text == stepLabels[i]);
            var x = stepX + i * stepWidth;
            if (number != null) number.Bounds = new Rectangle(x + Math.Max(0, (stepWidth - 24) / 2), 126, 24, 24);
            if (text != null) text.Bounds = new Rectangle(x, 151, stepWidth, 30);
        }
    }

    void ArrangeSummary(Control control)
    {
        if (control is not GRLCard card) return;
        var w = card.ClientSize.Width;
        var h = card.ClientSize.Height;
        var title = card.Controls.Cast<Control>().FirstOrDefault(c => c is Label l && l.Text == "CURRENT ANALYSIS SUMMARY");
        title?.SetBounds(18, 12, Math.Max(300, w - 36), 28);
        detectedGameIcon.Bounds = new Rectangle(20, 52, 58, 58);
        gameName.Bounds = new Rectangle(92, 53, Math.Max(260, w / 2 - 110), 34);
        gameMeta.Bounds = new Rectangle(92, 91, Math.Max(260, w / 2 - 110), Math.Max(58, h - 102));
        var connectionX = Math.Max(430, w / 2 + 12);
        connections.Bounds = new Rectangle(connectionX, 53, Math.Max(260, w - connectionX - 18), Math.Max(72, h - 66));
    }

    void ArrangeBest(Control control)
    {
        if (control is not GRLCard card) return;
        var w = card.ClientSize.Width;
        var h = card.ClientSize.Height;
        var title = card.Controls.Cast<Control>().FirstOrDefault(c => c is Label l && l.Text == "BEST ENDPOINT (CURRENT)");
        title?.SetBounds(18, 12, Math.Max(280, w / 2 - 28), 28);
        var leftWidth = Math.Max(330, (int)(w * .48));
        best.Bounds = new Rectangle(20, 48, leftWidth, 42);
        metrics.Bounds = new Rectangle(20, 94, leftWidth, Math.Max(82, h - 104));
        var graphX = Math.Min(leftWidth + 34, w / 2 + 10);
        quality.Bounds = new Rectangle(graphX, 12, Math.Max(150, w - graphX - 18), 30);
        quality.TextAlign = ContentAlignment.TopRight;
        graph.Bounds = new Rectangle(graphX, 52, Math.Max(260, w - graphX - 18), Math.Max(90, h - 62));
    }

    void ArrangeConsole(Control control)
    {
        if (control is not GRLCard card) return;
        card.Padding = new Padding(10, 34, 10, 10);
        var header = card.Controls.Cast<Control>().FirstOrDefault(c => c is Label l && l.Text == "LIVE ANALYSIS CONSOLE");
        header?.SetBounds(10, 7, Math.Max(220, card.ClientSize.Width - 20), 22);
        console.Bounds = new Rectangle(10, 34, Math.Max(100, card.ClientSize.Width - 20), Math.Max(60, card.ClientSize.Height - 44));
        console.BringToFront();
    }

    void ArrangeLeft(Control control)
    {
        if (control is not GRLCard panel) return;
        var w = panel.ClientSize.Width;
        var h = panel.ClientSize.Height;
        var buttonY = Math.Max(100, h - 88);
        var all = panel.Controls.Cast<Control>().FirstOrDefault(c => c is GRLActionButton b && b.Text == "VIEW ALL GAMES");
        var clear = panel.Controls.Cast<Control>().FirstOrDefault(c => c is GRLActionButton b && b.Text == "CLEAR MEMORY");
        all?.SetBounds(16, buttonY, w - 32, 40);
        clear?.SetBounds(16, buttonY + 48, w - 32, 40);
        games.Bounds = new Rectangle(10, 66, Math.Max(170, w - 20), Math.Max(90, buttonY - 76));
        foreach (Control c in games.Controls) c.Width = Math.Max(160, games.ClientSize.Width - 6);
    }

    void ArrangeRight(Control control)
    {
        if (control is not TableLayoutPanel right) return;
        right.Padding = Padding.Empty;
        right.RowStyles[0].SizeType = SizeType.Percent;
        right.RowStyles[1].SizeType = SizeType.Percent;
        right.RowStyles[2].SizeType = SizeType.Percent;
        right.RowStyles[0].Height = 31;
        right.RowStyles[1].Height = 34;
        right.RowStyles[2].Height = 35;
    }

    void InstallTelemetryCards(Control control)
    {
        if (control is not TableLayoutPanel right) return;
        telemetryOverlays.Clear();
        foreach (var card in right.Controls.OfType<GRLInfoCard>())
        {
            var overlay = new LiveTelemetryOverlay { Accent = card.Accent, Dock = DockStyle.Bottom, Height = 44 };
            card.Controls.Add(overlay);
            overlay.BringToFront();
            telemetryOverlays.Add(overlay);
        }
    }

    void HookEndpointAutomation()
    {
        if (endpointHooked) return;
        endpointHooked = true;
        var find = actions.FirstOrDefault(x => x.Text == "FIND CONNECTIONS");
        if (find != null) find.Click += (_, _) => ScheduleEndpointSelection();
        var auto = actions.FirstOrDefault(x => x.Text == "AUTO ANALYZE");
        if (auto != null) auto.Click += (_, _) => ScheduleEndpointSelection(1800);
    }

    void ScheduleEndpointSelection(int delay = 900)
    {
        var timer = new Timer { Interval = delay };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            await AutoPopulateEndpointAsync();
        };
        timer.Start();
    }

    async Task AutoPopulateEndpointAsync()
    {
        if (IsDisposed || DateTime.UtcNow - lastEndpointAttempt < TimeSpan.FromMilliseconds(600)) return;
        lastEndpointAttempt = DateTime.UtcNow;
        try
        {
            var found = await GameScanner.DiscoverAsync();
            var game = (current == null ? null : found.FirstOrDefault(x => x.Pid == GetCurrentPid()))
                       ?? found.Where(x => x.LikelyGame).OrderByDescending(x => x.Confidence).FirstOrDefault();
            if (game == null) return;

            current = GameProfileStore.Load().FirstOrDefault(p =>
                p.ProcessName.Equals(game.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                p.ExecutablePath.Equals(game.ExecutablePath, StringComparison.OrdinalIgnoreCase)) ?? current;
            if (current == null)
            {
                try { current = GameProfileStore.Touch(game.ProcessName, game.ExecutablePath); }
                catch { return; }
            }

            var endpoints = found.Where(x => x.Pid == game.Pid && x.LikelyGame && x.RemotePort > 0)
                .GroupBy(x => $"{x.RemoteIp}:{x.RemotePort}/{x.Protocol}")
                .Select(g => g.OrderByDescending(x => x.Confidence).First())
                .OrderByDescending(x => x.Confidence)
                .Take(12)
                .ToList();
            if (endpoints.Count == 0) return;

            var selected = endpoints[0];
            endpoint.Text = $"{selected.RemoteIp}:{selected.RemotePort}";
            best.Text = $"{selected.RemoteIp}:{selected.RemotePort}  ({selected.Protocol})";
            connections.Text = string.Join(Environment.NewLine, endpoints.Take(8).Select(x =>
                $"{x.Protocol,-3} {x.RemoteIp}:{x.RemotePort,-5}   {x.State,-11}  {x.Confidence}%"));
            Log($"AUTO ENDPOINT: {selected.RemoteIp}:{selected.RemotePort} ({selected.Protocol}) selected from {endpoints.Count} game connection(s).");
            UpdateDetectedGameVisual(current);
            StartLiveTracker(selected.RemoteIp, selected.RemotePort, selected.Protocol);
        }
        catch (Exception ex)
        {
            Log("[ENDPOINT AUTO] " + ex.Message);
        }
    }

    int GetCurrentPid()
    {
        if (current == null) return 0;
        try
        {
            var name = Path.GetFileNameWithoutExtension(current.ProcessName);
            return Process.GetProcessesByName(name).FirstOrDefault()?.Id ?? 0;
        }
        catch { return 0; }
    }

    void UpdateDetectedGameVisual(GameProfile? profile)
    {
        if (profile == null) return;
        gameName.Text = profile.DisplayName;
        gameMeta.Text = $"{profile.Observations} saved analyses\r\nRunning: YES\r\nPath: {profile.ExecutablePath}\r\nLast scan: {profile.LastSeenUtc.ToLocalTime():g}";
        try
        {
            if (!File.Exists(profile.IconPath)) return;
            using var source = Image.FromFile(profile.IconPath);
            var copy = new Bitmap(source);
            var old = detectedGameIcon.Image;
            detectedGameIcon.Image = copy;
            old?.Dispose();
        }
        catch { }
    }

    void StartLiveTracker(string ip, int port, string protocol)
    {
        liveLatency.Clear();
        liveJitter.Clear();
        liveLost = 0;
        lastLiveSample = -1;
        liveTarget = ip;
        best.Text = $"{ip}:{port}  ({protocol})";
        quality.Text = "TRACKING";
        quality.ForeColor = Cyan;
    }

    async void PolishTimer_Tick(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        try
        {
            foreach (var overlay in telemetryOverlays) overlay.Phase = phase;
            UpdateRightTelemetry();
            UpdateDetectedGameFromState();
            if (!string.IsNullOrWhiteSpace(liveTarget) && IPAddress.TryParse(liveTarget, out var ip))
                await SampleLivePing(ip);
            graph.Invalidate();
            foreach (var overlay in telemetryOverlays) overlay.Invalidate();
        }
        catch (Exception ex) { Log("[LIVE TRACKER] " + ex.Message); }
    }

    void UpdateDetectedGameFromState()
    {
        if (current != null && !gameName.Text.Equals(current.DisplayName, StringComparison.OrdinalIgnoreCase))
            UpdateDetectedGameVisual(current);
    }

    void UpdateRightTelemetry()
    {
        var root = Controls.OfType<TableLayoutPanel>().FirstOrDefault();
        if (root?.Controls[2] is not TableLayoutPanel body || body.Controls[2] is not TableLayoutPanel right) return;
        var cards = right.Controls.OfType<GRLInfoCard>().ToList();
        if (cards.Count < 2) return;
        var pulse = (Math.Sin(phase * 1.7) + 1) * .5;
        var netText = network.Text;
        cards[0].Content = string.IsNullOrWhiteSpace(netText) || netText.StartsWith("NETWORK:")
            ? "LIVE TELEMETRY\r\nISP       • scanning\r\nASN       • scanning\r\nPUBLIC IP • scanning\r\nGATEWAY   • scanning\r\nDNS       • scanning"
            : netText.Replace(" | ", Environment.NewLine);
        cards[1].Content = $"ROUTER LINK • {(pulse > .18 ? "ACTIVE" : "SYNC")}\r\nGateway     • {(netText.Contains("GW ") ? netText.Split("GW ").LastOrDefault() : "scanning")}\r\nRoute state • monitoring\r\nConfidence • LIVE";
        cards[0].Invalidate();
        cards[1].Invalidate();
    }

    async Task SampleLivePing(IPAddress ip)
    {
        using var pingClient = new System.Net.NetworkInformation.Ping();
        try
        {
            var reply = await pingClient.SendPingAsync(ip, 850);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                var ms = reply.RoundtripTime;
                liveLatency.Add(ms);
                if (liveLatency.Count > 30) liveLatency.RemoveAt(0);
                if (lastLiveSample >= 0) liveJitter.Add(Math.Abs(ms - lastLiveSample));
                if (liveJitter.Count > 29) liveJitter.RemoveAt(0);
                lastLiveSample = ms;
            }
            else liveLost++;
        }
        catch { liveLost++; }

        var total = liveLatency.Count + liveLost;
        var avg = liveLatency.Count == 0 ? 0 : liveLatency.Average();
        var jitter = liveJitter.Count == 0 ? 0 : liveJitter.Average();
        var loss = total == 0 ? 0 : liveLost * 100.0 / total;
        var stability = avg <= 0 ? "Waiting" : loss == 0 && jitter <= 5 && avg <= 80 ? "Excellent" : loss <= 2 && jitter <= 12 ? "Good" : "Fair";

        metrics.Text = $"LATENCY     {(avg > 0 ? avg.ToString("0") : "—")} ms\r\nLOSS        {loss:0.#}%\r\nJITTER      {(jitter > 0 ? jitter.ToString("0.#") : "—")} ms\r\nSTABILITY   {stability}";
        quality.Text = stability.ToUpperInvariant();
        quality.ForeColor = stability == "Excellent" ? Green : stability == "Good" ? Yellow : stability == "Waiting" ? Muted : Red;
        graph.Values = liveLatency.Count > 1 ? new List<double>(liveLatency) : new List<double> { avg, avg };
        graph.Invalidate();
    }
}

sealed class LiveTelemetryOverlay : Control
{
    public Color Accent { get; set; } = Color.Cyan;
    public float Phase { get; set; }

    public LiveTelemetryOverlay()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(6, 12, 25);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var w = Math.Max(1, ClientSize.Width);
        var h = Math.Max(1, ClientSize.Height);
        using var grid = new Pen(Color.FromArgb(18, 46, 70), 1);
        e.Graphics.DrawLine(grid, 0, h - 8, w, h - 8);
        var pulse = (float)((Math.Sin(Phase * 1.4) + 1) * .5);
        using var bar = new SolidBrush(Color.FromArgb(90 + (int)(90 * pulse), Accent.R, Accent.G, Accent.B));
        var fill = Math.Max(8, (int)(w * (.30f + .12f * pulse)));
        e.Graphics.FillRectangle(bar, 0, h - 5, fill, 3);
        using var scan = new Pen(Color.FromArgb(120, Accent.R, Accent.G, Accent.B), 1);
        var x = (float)((Math.Sin(Phase * .9) + 1) * .5 * Math.Max(1, w - 1));
        e.Graphics.DrawLine(scan, x, 2, x, h - 9);
        using var dot = new SolidBrush(Accent);
        e.Graphics.FillEllipse(dot, Math.Max(0, x - 2), 2, 4, 4);
    }
}
