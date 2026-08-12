using System.Reflection;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms;

namespace CrossFireRouteLab;

internal static class TelemetryVisibilityPatch
{
    static readonly Color Surface = Color.FromArgb(7, 13, 27);
    static readonly Color Cyan = Color.FromArgb(0, 225, 255);
    static readonly Color Purple = Color.FromArgb(188, 72, 255);
    static readonly Color Green = Color.FromArgb(40, 242, 122);
    static readonly Color TextColor = Color.FromArgb(238, 246, 255);
    static readonly Color Muted = Color.FromArgb(132, 157, 190);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static void Apply(Form form)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();
        var network = type.GetField("networkPanel", flags)?.GetValue(form) as Control;
        var router = type.GetField("routerPanel", flags)?.GetValue(form) as Control;
        if (network == null || router == null) return;
        Install(network, "NETWORK TELEMETRY", Cyan, true);
        Install(router, "ROUTER INTELLIGENCE", Purple, false);
    }

    static void Install(Control host, string title, Color accent, bool serverTracker)
    {
        host.Visible = true;
        host.Dock = DockStyle.Fill;
        host.MinimumSize = new Size(220, 150);
        host.Margin = Padding.Empty;
        host.Padding = Padding.Empty;
        host.BackColor = Surface;
        foreach (Control c in host.Controls.Cast<Control>().ToArray())
            if (c.Tag is string tag && tag.StartsWith("grl-visible-fix", StringComparison.Ordinal)) host.Controls.Remove(c);
        var surface = new TelemetrySurface(title, accent, serverTracker) { Tag = "grl-visible-fix-surface", Dock = DockStyle.Fill };
        host.Controls.Add(surface);
        surface.BringToFront();
    }

    sealed class TelemetrySurface : Control
    {
        readonly Color accent;
        readonly bool serverTracker;
        readonly Label state = new(), body = new(), tracker = new();
        readonly Timer timer = new() { Interval = 70 };
        float phase;

        public TelemetrySurface(string title, Color accent, bool serverTracker)
        {
            this.accent = accent; this.serverTracker = serverTracker;
            DoubleBuffered = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Surface;
            state.Text = "● WAITING"; state.ForeColor = Muted; state.Font = new Font("Segoe UI Semibold", 7.5f); state.AutoSize = false; state.TextAlign = ContentAlignment.MiddleRight;
            body.ForeColor = TextColor; body.BackColor = Color.Transparent; body.Font = new Font("Cascadia Mono", 7.7f); body.AutoEllipsis = false;
            body.Text = serverTracker ? "Local interface telemetry\r\nPress DETECT NETWORK to populate." : "Gateway / interface telemetry\r\nPress DETECT ROUTER to populate.";
            tracker.ForeColor = TextColor; tracker.BackColor = Color.Transparent; tracker.Font = new Font("Cascadia Mono", 7.5f); tracker.Text = "ISP / SERVER TRACKER\r\nWaiting for a selected game endpoint…";
            Controls.Add(state); Controls.Add(body); if (serverTracker) Controls.Add(tracker);
            timer.Tick += async (_, _) => await Tick(); timer.Start(); Disposed += (_, _) => timer.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var border = new Pen(Color.FromArgb(170, accent), 1); e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
            using var titleBrush = new SolidBrush(accent); using var titleFont = new Font("Segoe UI Semibold", 10);
            e.Graphics.DrawString(serverTracker ? "NETWORK TELEMETRY" : "ROUTER INTELLIGENCE", titleFont, titleBrush, 14, 10);
            using var grid = new Pen(Color.FromArgb(40, accent), 1); int top = 40; int bottom = serverTracker ? Math.Max(108, Height / 2) : Height - 14;
            for (int y = top; y <= bottom; y += 18) e.Graphics.DrawLine(grid, 14, y, Math.Max(14, Width - 14), y);
            float x = 14 + ((MathF.Sin(phase) + 1f) * .5f) * Math.Max(1, Width - 28);
            using var pulse = new Pen(Color.FromArgb(210, accent), 2); e.Graphics.DrawLine(pulse, x, top, x, Math.Max(top, bottom));
            if (serverTracker) { using var divider = new Pen(Color.FromArgb(85, accent), 1); e.Graphics.DrawLine(divider, 14, Math.Max(106, Height / 2), Width - 14, Math.Max(106, Height / 2)); }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e); int w = Math.Max(80, Width - 28); state.SetBounds(14, 11, w, 20);
            int divider = serverTracker ? Math.Max(106, Height / 2) : Height - 14;
            body.SetBounds(14, 42, w, Math.Max(44, divider - 48));
            if (serverTracker) tracker.SetBounds(14, divider + 8, w, Math.Max(40, Height - divider - 18));
            Invalidate();
        }

        async Task Tick()
        {
            if (IsDisposed) return; phase += .12f; var form = FindForm(); if (form == null) return;
            var flags = BindingFlags.Instance | BindingFlags.NonPublic; var type = form.GetType();
            string? endpoint = type.GetField("endpoint", flags)?.GetValue(form) as string;
            int port = type.GetField("endpointPort", flags)?.GetValue(form) is int p ? p : 0;
            var network = type.GetField("lastNetwork", flags)?.GetValue(form) as string; var router = type.GetField("lastRouter", flags)?.GetValue(form) as string;
            body.Text = serverTracker ? (string.IsNullOrWhiteSpace(network) ? "Waiting for network scan…" : network) : (string.IsNullOrWhiteSpace(router) ? "Waiting for router scan…" : router);
            state.Text = serverTracker ? (network?.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase) == false ? "● LIVE" : "● SCANNING") : (router?.StartsWith("Waiting", StringComparison.OrdinalIgnoreCase) == false ? "● LIVE" : "● SCANNING");
            state.ForeColor = state.Text.Contains("LIVE", StringComparison.Ordinal) ? Green : Muted;
            if (serverTracker)
            {
                if (string.IsNullOrWhiteSpace(endpoint)) tracker.Text = "ISP / SERVER TRACKER\r\nWaiting for a selected game endpoint…";
                else
                {
                    var cached = tracker.Tag as TrackerResult;
                    if (cached != null && cached.Ip == endpoint && (DateTime.UtcNow - cached.Time).TotalSeconds < 60) tracker.Text = cached.Display(port);
                    else
                    {
                        tracker.Text = $"ISP / SERVER TRACKER\r\nIP        {endpoint}:{port}\r\nLOOKUP    resolving country / ISP…";
                        try { var info = await Lookup(endpoint); var result = new TrackerResult(endpoint, DateTime.UtcNow, info.Country, info.City, info.Isp, info.Asn); tracker.Tag = result; tracker.Text = result.Display(port); }
                        catch { tracker.Text = $"ISP / SERVER TRACKER\r\nIP        {endpoint}:{port}\r\nCOUNTRY   lookup unavailable\r\nISP       lookup unavailable"; }
                    }
                }
            }
            Invalidate();
        }

        static async Task<Geo> Lookup(string ip)
        {
            using var response = await Http.GetAsync("https://ipwho.is/" + Uri.EscapeDataString(ip)); response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var root = doc.RootElement;
            return new Geo(Get(root, "country"), Get(root, "city"), Get(root, "connection", "isp"), Get(root, "connection", "asn"));
        }
        static string Get(JsonElement root, params string[] path) { var cur = root; foreach (var p in path) { if (!cur.TryGetProperty(p, out cur)) return "—"; } return cur.ValueKind == JsonValueKind.String ? cur.GetString() ?? "—" : cur.ToString(); }
        sealed record Geo(string Country, string City, string Isp, string Asn);
        sealed record TrackerResult(string Ip, DateTime Time, string Country, string City, string Isp, string Asn)
        { public string Display(int port) => $"ISP / SERVER TRACKER\r\nIP        {Ip}:{port}\r\nCOUNTRY   {Country}\r\nCITY      {City}\r\nISP       {Isp}\r\nASN       {Asn}"; }
    }
}
