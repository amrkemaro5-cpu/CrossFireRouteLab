using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace CrossFireRouteLab;

internal static class IspTrackerPatch
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    static readonly Color Text = Color.FromArgb(238, 246, 255);
    static readonly Color Cyan = Color.FromArgb(0, 225, 255);

    public static void Apply(Form form)
    {
        if (form.IsDisposed) return;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();
        var network = type.GetField("networkPanel", flags)?.GetValue(form) as Control;
        var router = type.GetField("routerPanel", flags)?.GetValue(form) as Control;
        var networkText = type.GetField("networkText", flags)?.GetValue(form) as Label;
        var routerText = type.GetField("routerText", flags)?.GetValue(form) as Label;
        if (network == null || router == null || networkText == null || routerText == null) return;

        if (!network.Controls.Contains(networkText)) network.Controls.Add(networkText);
        if (!router.Controls.Contains(routerText)) router.Controls.Add(routerText);

        var tracker = new Label
        {
            Name = "serverIspTracker",
            AutoEllipsis = false,
            BackColor = Color.Transparent,
            ForeColor = Text,
            Font = new Font("Cascadia Mono", 7.6f),
            Text = "ISP / SERVER TRACKER\r\nWaiting for a selected game endpoint…",
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            Tag = "grl-server-isp-tracker"
        };
        var divider = new Panel { BackColor = Color.FromArgb(45, Cyan), Height = 1, Tag = "grl-isp-divider" };
        network.Controls.Add(divider);
        network.Controls.Add(tracker);

        var timer = new System.Windows.Forms.Timer { Interval = 1200 };
        timer.Tick += async (_, _) => await Tick(form, network, networkText, tracker, divider);
        timer.Start();
        form.FormClosed += (_, _) => timer.Stop();
        form.Resize += (_, _) => Layout(network, networkText, tracker, divider);
        Layout(network, networkText, tracker, divider);
    }

    static void Layout(Control panel, Label networkText, Label tracker, Control divider)
    {
        if (panel.ClientSize.Width < 100) return;
        int top = 43;
        int dividerY = Math.Max(98, panel.ClientSize.Height / 2 + 3);
        networkText.SetBounds(14, top, Math.Max(100, panel.ClientSize.Width - 28), Math.Max(42, dividerY - top - 7));
        divider.SetBounds(14, dividerY, Math.Max(80, panel.ClientSize.Width - 28), 1);
        tracker.SetBounds(14, dividerY + 8, Math.Max(100, panel.ClientSize.Width - 28), Math.Max(38, panel.ClientSize.Height - dividerY - 18));
    }

    static async Task Tick(Form form, Control panel, Label networkText, Label tracker, Control divider)
    {
        if (form.IsDisposed || !form.IsHandleCreated) return;
        Layout(panel, networkText, tracker, divider);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = form.GetType();
        var endpoint = type.GetField("endpoint", flags)?.GetValue(form) as string;
        var portValue = type.GetField("endpointPort", flags)?.GetValue(form);
        int port = portValue is int p ? p : 0;

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            tracker.Text = "ISP / SERVER TRACKER\r\nWaiting for a selected game endpoint…";
            return;
        }

        if (tracker.Tag is TrackerState state && state.Ip == endpoint && (DateTime.UtcNow - state.Time).TotalSeconds < 60)
        {
            tracker.Text = state.Display(port);
            return;
        }

        tracker.Text = $"ISP / SERVER TRACKER\r\nIP        {endpoint}:{port}\r\nLOOKUP    resolving server country / ISP…";
        try
        {
            var info = await Lookup(endpoint);
            var next = new TrackerState(endpoint, DateTime.UtcNow, info.Country, info.City, info.Isp, info.Asn);
            tracker.Tag = next;
            tracker.Text = next.Display(port);
        }
        catch
        {
            tracker.Text = $"ISP / SERVER TRACKER\r\nIP        {endpoint}:{port}\r\nCOUNTRY   lookup unavailable\r\nISP       lookup unavailable";
        }
    }

    static async Task<GeoInfo> Lookup(string ip)
    {
        using var response = await Http.GetAsync("https://ipwho.is/" + Uri.EscapeDataString(ip));
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            throw new InvalidOperationException("Geo lookup failed");
        return new GeoInfo(Get(root, "country"), Get(root, "city"), Get(root, "connection", "isp"), Get(root, "connection", "asn"));
    }

    static string Get(JsonElement root, params string[] path)
    {
        var cur = root;
        foreach (var p in path)
        {
            if (!cur.TryGetProperty(p, out cur)) return "—";
        }
        return cur.ValueKind == JsonValueKind.String ? cur.GetString() ?? "—" : cur.ToString();
    }

    sealed record GeoInfo(string Country, string City, string Isp, string Asn);
    sealed record TrackerState(string Ip, DateTime Time, string Country, string City, string Isp, string Asn)
    {
        public string Display(int port) => $"ISP / SERVER TRACKER\r\nIP        {Ip}:{port}\r\nCOUNTRY   {Country}\r\nCITY      {City}\r\nISP       {Isp}\r\nASN       {Asn}";
    }
}
