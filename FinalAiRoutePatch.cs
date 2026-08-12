using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Final one-click route optimizer. The "AI" is an adaptive deterministic
/// scoring engine using measured latency, jitter and loss. It only changes a
/// /32 route for the verified CrossFire TCP room endpoint; it never changes
/// the whole Windows default route.
/// </summary>
internal static class FinalAiRoutePatch
{
    static bool installed;
    static int running;
    static Button? optimizeButton;
    static Label? statusLabel;
    static System.Threading.Timer? monitor;
    static string? managedIp;
    static DefaultRoute? managedRoute;
    static readonly string Store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameRouteLab", "route-ai-history.json");

    const int Samples = 7;
    const int TimeoutMs = 1200;
    const double MinimumImprovementMs = 3.0;
    const double MinimumImprovementPct = 0.05;

    public static void Apply(Form form)
    {
        if (installed || form.IsDisposed) return;
        installed = true;
        Install(form);
    }

    static void Install(Form form)
    {
        // Replace the old one-click button so the legacy analysis handlers
        // cannot launch several competing passes at the same time.
        var legacy = Find(form, "AUTO ANALYZE");
        if (legacy != null)
        {
            var parent = legacy.Parent;
            var bounds = legacy.Bounds;
            legacy.Visible = false;
            optimizeButton = new Button
            {
                Text = "AI OPTIMIZE ROUTE",
                Bounds = bounds,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(7, 13, 27),
                Font = legacy.Font,
                TabIndex = legacy.TabIndex
            };
            optimizeButton.FlatAppearance.BorderColor = Color.FromArgb(40, 242, 122);
            optimizeButton.FlatAppearance.BorderSize = 1;
            optimizeButton.Click += async (_, _) => await RunOneClick(form);
            parent?.Controls.Add(optimizeButton);
            optimizeButton.BringToFront();
        }

        AddStatus(form);
        monitor = new System.Threading.Timer(_ => PassiveHealthCheck(form), null, 15000, 15000);
        Log(form, "[AI ROUTE] One-click optimizer ready. TCP-only CrossFire room routing; no UDP probes; no synthetic game packets.");
    }

    static async Task RunOneClick(Form form)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) return;
        SetButton(false);
        try
        {
            SetStatus("AI OPTIMIZING • MEASURING ROUTES");
            Log(form, "[AI ROUTE] Starting complete route optimization. Keep CrossFire inside the active room.");

            if (!IsCrossFire(form))
            {
                Fail(form, "CrossFire is not the active game. Start CrossFire and enter an online room first.");
                return;
            }

            if (!TryRoom(out var ip, out var port, out var protocol) || !protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
            {
                Fail(form, "No verified CrossFire TCP room endpoint is available yet.");
                return;
            }

            Log(form, $"[AI ROUTE] TARGET {ip}:{port} TCP — verified from CrossFire live connections.");

            var routes = await ReadDefaultRoutes();
            if (routes.Count == 0)
            {
                Fail(form, "No active IPv4 default route was found.");
                return;
            }

            var baseline = await MeasureTcp(ip, port, Samples);
            Log(form, FormatMeasurement("CURRENT PATH", baseline));

            var candidates = new List<RouteCandidate>();
            foreach (var route in routes)
            {
                if (!await AddTestRoute(ip, route))
                {
                    Log(form, $"[AI ROUTE] SKIP {route.Alias} → {route.Gateway}: Windows rejected the test route.");
                    continue;
                }

                Measurement measurement;
                try { measurement = await MeasureTcp(ip, port, Samples); }
                finally { await RemoveTestRoute(ip); }

                var score = Score(measurement);
                candidates.Add(new RouteCandidate(route, measurement, score));
                Log(form, $"[AI ROUTE] TEST {route.Alias} → {route.Gateway}: {FormatMeasurement("", measurement)} score={score:0.0}.");
            }

            var baselineScore = Score(baseline);
            var best = candidates.OrderBy(c => c.Score).FirstOrDefault();
            if (best == null)
            {
                Fail(form, "No candidate route could be measured. The current route was left untouched.");
                return;
            }

            var improvementMs = baseline.MedianMs >= 0 && best.Measurement.MedianMs >= 0
                ? baseline.MedianMs - best.Measurement.MedianMs : 0;
            var improvementPct = baseline.MedianMs > 0 ? improvementMs / baseline.MedianMs : 0;
            var materiallyBetter = best.Score + Math.Max(0.5, baselineScore * 0.02) < baselineScore
                && (improvementMs >= MinimumImprovementMs || improvementPct >= MinimumImprovementPct);

            if (!materiallyBetter)
            {
                SetStatus("AI ROUTE • CURRENT PATH RETAINED");
                Log(form, $"[AI ROUTE] DECISION: keep current route. Best candidate is not materially better (Δ {improvementMs:0.0} ms / {improvementPct:P0}).");
                SaveHistory(ip, port, baseline, null, "retained-current");
                return;
            }

            Log(form, $"[AI ROUTE] WINNER: {best.Route.Alias} → {best.Route.Gateway}; estimated improvement {improvementMs:0.0} ms ({improvementPct:P0}).");
            Log(form, "[AI ROUTE] APPLYING ONLY A /32 ROUTE FOR THIS CrossFire ROOM SERVER.");

            if (!await AddPersistentRoomRoute(ip, best.Route))
            {
                Fail(form, "The best route was measurable but Windows refused to apply the CrossFire /32 route. Current route remains active.");
                SaveHistory(ip, port, baseline, best, "apply-failed");
                return;
            }

            var verified = await MeasureTcp(ip, port, Samples);
            Log(form, FormatMeasurement("APPLIED PATH", verified));

            if (!IsBetter(verified, baseline))
            {
                Log(form, "[AI ROUTE] VERIFICATION FAILED: applied route did not beat the baseline. Rolling it back immediately.");
                await RemovePersistentRoomRoute(ip, best.Route);
                SetStatus("AI ROUTE • ROLLED BACK");
                SaveHistory(ip, port, baseline, best, "rolled-back");
                return;
            }

            managedIp = ip;
            managedRoute = best.Route;
            SetStatus("AI ROUTE • ACTIVE • VERIFIED");
            Log(form, $"[AI ROUTE] SUCCESS: {ip}:{port} is now routed through {best.Route.Alias} → {best.Route.Gateway}.");
            Log(form, $"[AI ROUTE] VERIFIED improvement: {baseline.MedianMs:0.0} ms → {verified.MedianMs:0.0} ms; jitter {verified.JitterMs:0.0} ms; loss {verified.LossPct:0.0}%.");
            Log(form, "[AI ROUTE] NOTE: existing TCP sessions cannot be moved by Windows. Rejoin the CrossFire room if the game keeps the old socket.");
            SaveHistory(ip, port, baseline, best, "applied");
        }
        catch (Exception ex)
        {
            Fail(form, "Optimizer error: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref running, 0);
            SetButton(true);
        }
    }

    static bool IsBetter(Measurement after, Measurement before)
    {
        if (after.MedianMs < 0 || before.MedianMs < 0) return false;
        return after.MedianMs + MinimumImprovementMs < before.MedianMs
            && after.LossPct <= before.LossPct + 5
            && after.JitterMs <= Math.Max(before.JitterMs + 2, before.JitterMs * 1.25);
    }

    static double Score(Measurement m)
    {
        if (m.MedianMs < 0) return 1_000_000;
        return m.MedianMs
            + m.JitterMs * 0.75
            + Math.Max(0, m.P95Ms - m.MedianMs) * 0.35
            + m.LossPct * 8.0;
    }

    static async Task<Measurement> MeasureTcp(string ip, int port, int count)
    {
        var values = new List<double>();
        var failures = 0;
        for (var i = 0; i < count; i++)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var c = new TcpClient { NoDelay = true };
                var task = c.ConnectAsync(ip, port);
                if (await Task.WhenAny(task, Task.Delay(TimeoutMs)) == task && c.Connected)
                {
                    sw.Stop();
                    values.Add(sw.Elapsed.TotalMilliseconds);
                }
                else failures++;
            }
            catch { failures++; }
            await Task.Delay(80);
        }

        if (values.Count == 0) return new Measurement(-1, -1, -1, 100, count, 0);
        values.Sort();
        var median = Percentile(values, 0.50);
        var p95 = Percentile(values, 0.95);
        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        var jitter = Math.Sqrt(variance);
        var loss = failures * 100.0 / count;
        return new Measurement(median, p95, jitter, loss, count, values.Count);
    }

    static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];
        var index = (sorted.Count - 1) * p;
        var lo = (int)Math.Floor(index);
        var hi = (int)Math.Ceiling(index);
        return lo == hi ? sorted[lo] : sorted[lo] + (sorted[hi] - sorted[lo]) * (index - lo);
    }

    static async Task<List<DefaultRoute>> ReadDefaultRoutes()
    {
        const string cmd = "Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -PolicyStore ActiveStore | ForEach-Object { $a=Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue; [pscustomobject]@{InterfaceIndex=$_.InterfaceIndex;Alias=$a.Name;Gateway=$_.NextHop;Metric=$_.RouteMetric;Up=($a.Status -eq 'Up')} } | ConvertTo-Json -Compress";
        var output = await RunPowerShell(cmd, 8000);
        var result = new List<DefaultRoute>();
        try
        {
            using var doc = JsonDocument.Parse(output.Trim());
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { doc.RootElement };
            foreach (var x in items)
            {
                var route = new DefaultRoute(ReadInt(x, "InterfaceIndex"), Read(x, "Alias"), Read(x, "Gateway"), ReadInt(x, "Metric"), x.TryGetProperty("Up", out var up) && up.GetBoolean());
                if (route.Up && route.InterfaceIndex > 0 && IPAddress.TryParse(route.Gateway, out var gw) && !gw.Equals(IPAddress.Any))
                    result.Add(route);
            }
        }
        catch { }
        return result.GroupBy(r => (r.InterfaceIndex, r.Gateway)).Select(g => g.OrderBy(r => r.Metric).First()).ToList();
    }

    static async Task<bool> AddTestRoute(string ip, DefaultRoute route)
    {
        await RemoveTestRoute(ip);
        var cmd = $"New-NetRoute -DestinationPrefix '{ip}/32' -InterfaceIndex {route.InterfaceIndex} -NextHop '{route.Gateway}' -RouteMetric 1 -PolicyStore ActiveStore -ErrorAction Stop | Out-Null; Write-Output 'OK'";
        var output = await RunPowerShell(cmd, 6000);
        return output.Contains("OK", StringComparison.OrdinalIgnoreCase);
    }

    static async Task<bool> AddPersistentRoomRoute(string ip, DefaultRoute route) => await AddTestRoute(ip, route);

    static async Task RemoveTestRoute(string ip)
    {
        var cmd = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{ $_.RouteMetric -eq 1 -and $_.Protocol -eq 3 }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";
        await RunPowerShell(cmd, 6000);
    }

    static async Task RemovePersistentRoomRoute(string ip, DefaultRoute route)
    {
        var cmd = $"Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '{ip}/32' -PolicyStore ActiveStore -ErrorAction SilentlyContinue | Where-Object {{ $_.InterfaceIndex -eq {route.InterfaceIndex} -and $_.NextHop -eq '{route.Gateway}' }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue";
        await RunPowerShell(cmd, 6000);
    }

    static void PassiveHealthCheck(Form form)
    {
        if (Volatile.Read(ref running) != 0 || !IsCrossFire(form)) return;
        if (!TryRoom(out var ip, out var port, out var protocol) || !protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase)) return;

        if (managedIp != null && managedRoute.HasValue && !managedIp.Equals(ip, StringComparison.OrdinalIgnoreCase))
        {
            var oldIp = managedIp;
            var oldRoute = managedRoute.Value;
            managedIp = null;
            managedRoute = null;
            _ = RemovePersistentRoomRoute(oldIp, oldRoute);
            Log(form, $"[AI ROUTE] ROOM CHANGED: removed the old /32 route for {oldIp}; the new room will be evaluated normally.");
        }

        _ = Task.Run(async () =>
        {
            var m = await MeasureTcp(ip, port, 3);
            Log(form, $"[AI ROUTE] HEALTH {ip}:{port}: {(m.MedianMs < 0 ? "unreachable" : $"{m.MedianMs:0.0} ms median, {m.JitterMs:0.0} ms jitter, {m.LossPct:0.0}% loss")}");
        });
    }

    static void SaveHistory(string ip, int port, Measurement baseline, RouteCandidate? winner, string decision)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Store)!);
            var history = LoadHistory();
            history.Add(new HistoryItem
            {
                TimeUtc = DateTime.UtcNow,
                Ip = ip,
                Port = port,
                BaselineMedianMs = baseline.MedianMs,
                Winner = winner?.Route.Alias ?? "",
                WinnerGateway = winner?.Route.Gateway ?? "",
                WinnerMedianMs = winner?.Measurement.MedianMs ?? -1,
                Decision = decision
            });
            File.WriteAllText(Store, JsonSerializer.Serialize(history.TakeLast(100), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    static List<HistoryItem> LoadHistory()
    {
        try { return File.Exists(Store) ? JsonSerializer.Deserialize<List<HistoryItem>>(File.ReadAllText(Store)) ?? new() : new(); }
        catch { return new(); }
    }

    static string FormatMeasurement(string label, Measurement m)
    {
        if (m.MedianMs < 0) return $"[AI ROUTE] {label}: unavailable ({m.LossPct:0}% loss).";
        return $"[AI ROUTE] {label}: median {m.MedianMs:0.0} ms, p95 {m.P95Ms:0.0} ms, jitter {m.JitterMs:0.0} ms, loss {m.LossPct:0.0}% ({m.Successes}/{m.Attempts}).";
    }

    static void AddStatus(Form form)
    {
        var header = form.Controls.Cast<Control>().FirstOrDefault(c => c.Controls.Cast<Control>().Any(x => x.Text == "GAME ROUTE LAB"));
        if (header == null) return;
        statusLabel = new Label
        {
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "AI ROUTE ENGINE • READY",
            ForeColor = Color.FromArgb(40, 242, 122),
            BackColor = Color.FromArgb(7, 13, 27),
            Bounds = new Rectangle(1220, 88, 240, 24),
            Font = new Font("Segoe UI Semibold", 8f)
        };
        header.Controls.Add(statusLabel);
        statusLabel.BringToFront();
    }

    static void SetStatus(string text)
    {
        try { if (statusLabel != null && !statusLabel.IsDisposed) statusLabel.BeginInvoke((Action)(() => statusLabel.Text = text)); } catch { }
    }

    static void SetButton(bool enabled)
    {
        try { if (optimizeButton != null && !optimizeButton.IsDisposed) optimizeButton.BeginInvoke((Action)(() => optimizeButton.Enabled = enabled)); } catch { }
    }

    static void Fail(Form form, string message)
    {
        SetStatus("AI ROUTE • NO CHANGE");
        Log(form, "[AI ROUTE] " + message);
    }

    static bool TryRoom(out string ip, out int port, out string protocol)
    {
        ip = ""; port = 0; protocol = "";
        try { return CrossFireRoomTransportProbeV3.TryGetTarget(out ip, out port, out protocol); }
        catch { return false; }
    }

    static bool IsCrossFire(Form f) => (f.GetType().GetField("gameName", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(f)?.ToString() ?? "").Contains("crossfire", StringComparison.OrdinalIgnoreCase);
    static Button? Find(Control root, string text) => All(root).OfType<Button>().FirstOrDefault(b => b.Text.Equals(text, StringComparison.OrdinalIgnoreCase));

    static IEnumerable<Control> All(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var n in All(c)) yield return n;
        }
    }

    static void Log(Form f, string message)
    {
        try
        {
            if (f.IsDisposed) return;
            f.BeginInvoke((Action)(() => f.GetType().GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(f, new object[] { message })));
        }
        catch { }
    }

    static async Task<string> RunPowerShell(string command, int timeout)
    {
        return await Task.Run(() =>
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " + Quote(command),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                p.Start();
                if (!p.WaitForExit(timeout)) { try { p.Kill(); } catch { } return "timeout"; }
                return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            }
            catch { return ""; }
        });
    }

    static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    static string Read(JsonElement x, string name) => x.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p.ToString() : "";
    static int ReadInt(JsonElement x, string name) => x.TryGetProperty(name, out var p) && p.TryGetInt32(out var v) ? v : 0;

    readonly record struct DefaultRoute(int InterfaceIndex, string Alias, string Gateway, int Metric, bool Up);
    readonly record struct Measurement(double MedianMs, double P95Ms, double JitterMs, double LossPct, int Attempts, int Successes);
    readonly record struct RouteCandidate(DefaultRoute Route, Measurement Measurement, double Score);

    sealed class HistoryItem
    {
        public DateTime TimeUtc { get; set; }
        public string Ip { get; set; } = "";
        public int Port { get; set; }
        public double BaselineMedianMs { get; set; }
        public string Winner { get; set; } = "";
        public string WinnerGateway { get; set; } = "";
        public double WinnerMedianMs { get; set; }
        public string Decision { get; set; } = "";
    }
}
