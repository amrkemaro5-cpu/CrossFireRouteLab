using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace CrossFireRouteLab;

public sealed record NetworkProfile(
    string Gateway,
    string InterfaceName,
    string LocalIp,
    string WanType,
    string PublicIp,
    string ISP,
    string Organization,
    string ASN,
    string Country,
    string City,
    string DnsServers,
    string Notes);

public static class NetworkProfileDetector
{
    public static async Task<NetworkProfile> DetectAsync()
    {
        var best = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => new { n, p = n.GetIPProperties() })
            .Select(x => new
            {
                x.n,
                x.p,
                Gateway = x.p.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork),
                Local = x.p.UnicastAddresses.Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            })
            .Where(x => x.Gateway != null && x.Local != null)
            .OrderByDescending(x => x.n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .FirstOrDefault();

        var gateway = best?.Gateway?.ToString() ?? "Unknown";
        var local = best?.Local?.ToString() ?? "Unknown";
        var iface = best?.n.Name ?? "Unknown";
        var dns = best == null ? "Unknown" : string.Join(", ", best.p.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork));
        var wanType = best?.n.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Ppp => "PPPoE/PPP",
            NetworkInterfaceType.Ethernet => "Ethernet",
            NetworkInterfaceType.Wireless80211 => "Wi-Fi",
            _ => best?.n.NetworkInterfaceType.ToString() ?? "Unknown"
        };

        string publicIp = "", isp = "", org = "", asn = "", country = "", city = "";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("GameRouteLab/3.0 network-profile");
            using var doc = JsonDocument.Parse(await client.GetStringAsync("https://ipwho.is/"));
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) && !ok.GetBoolean()) throw new Exception("IP lookup rejected");
            publicIp = Get(root, "ip"); country = Get(root, "country"); city = Get(root, "city");
            if (root.TryGetProperty("connection", out var c)) { isp = Get(c, "isp"); org = Get(c, "org"); asn = Get(c, "asn"); }
        }
        catch { }

        var note = string.IsNullOrWhiteSpace(publicIp)
            ? "Local network detected. Public ISP enrichment unavailable/offline."
            : "ISP/ASN enrichment uses a read-only public-IP lookup; no router credentials are sent.";

        return new NetworkProfile(gateway, iface, local, wanType,
            publicIp.Length > 0 ? publicIp : "Unavailable",
            isp.Length > 0 ? isp : "Unavailable",
            org.Length > 0 ? org : "Unavailable",
            asn.Length > 0 ? asn : "Unavailable",
            country.Length > 0 ? country : "Unavailable",
            city.Length > 0 ? city : "Unavailable", dns, note);
    }

    static string Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";
}
