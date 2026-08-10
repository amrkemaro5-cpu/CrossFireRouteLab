using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace CrossFireRouteLab;

public sealed record RouterInfo(string Gateway, string Vendor, string Model, string Firmware, string ManagementUrl, string Confidence, string Notes);

public static class RouterDetector
{
    public static async Task<RouterInfo> DetectAsync()
    {
        var gateway = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(gateway)) return new RouterInfo("Unknown", "Unknown", "Unknown", "Unknown", "", "Low", "No IPv4 default gateway detected.");

        foreach (var scheme in new[] { "http", "https" })
        {
            var url = $"{scheme}://{gateway}/";
            try
            {
                using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("GameRouteLab/2.0 router-fingerprint");
                var html = await client.GetStringAsync(url);
                var title = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value;
                var combined = WebUtility.HtmlDecode(title + " " + html);
                var model = Regex.Match(combined, @"\b(TD-[A-Z0-9-]+|Archer\s+[A-Z0-9-]+|Deco\s+[A-Z0-9-]+)\b", RegexOptions.IgnoreCase).Groups[1].Value;
                var firmware = Regex.Match(combined, @"Firmware(?:\s+Version)?\s*[:\-]?\s*([0-9]+(?:\.[0-9]+){1,3}[^<\r\n]{0,60})", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(firmware)) firmware = Regex.Match(combined, @"\b(\d+\.\d+\.\d+(?:\s+Build\s+\d+[^<\r\n]{0,30})?)\b", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                var vendor = combined.Contains("TP-Link", StringComparison.OrdinalIgnoreCase) ? "TP-Link" : "Unknown";
                if (!string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(vendor)) return new RouterInfo(gateway, vendor, string.IsNullOrWhiteSpace(model) ? "Detected router; model not exposed without login" : model, string.IsNullOrWhiteSpace(firmware) ? "Not exposed without login" : firmware, url, string.IsNullOrWhiteSpace(model) ? "Medium" : "High", "Read-only fingerprint. No router credentials were requested or stored.");
            }
            catch { }
        }
        return new RouterInfo(gateway, "Unknown", "Unknown", "Not exposed", $"http://{gateway}/", "Low", "Gateway detected, but its management page did not expose a recognizable model without authentication.");
    }
}
