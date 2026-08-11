namespace CrossFireRouteLab;

// Runtime usability layer. It does not change network settings; it keeps the
// dashboard synchronized with the game process and the last measured endpoint.
public sealed partial class DashboardForm
{
    readonly System.Windows.Forms.Timer liveDiscoveryTimer = new() { Interval = 4000 };
    bool liveDiscoveryBusy;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SyncEndpointFromMemory();
        liveDiscoveryTimer.Tick -= LiveDiscoveryTimer_Tick;
        liveDiscoveryTimer.Tick += LiveDiscoveryTimer_Tick;
        liveDiscoveryTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        liveDiscoveryTimer.Stop();
        liveDiscoveryTimer.Dispose();
        base.OnFormClosed(e);
    }

    async void LiveDiscoveryTimer_Tick(object? sender, EventArgs e)
    {
        if (liveDiscoveryBusy || busy || IsDisposed || !IsHandleCreated) return;
        liveDiscoveryBusy = true;
        try
        {
            var candidates = await DiscoverGames();
            if (current == null && candidates.Count > 0)
            {
                current = candidates[0];
                gameName.Text = current.DisplayName;
                gameMeta.Text = $"{current.Observations} saved analyses\r\nPath: {current.ExecutablePath}\r\nBest: {(string.IsNullOrWhiteSpace(current.LastBestEndpoint) ? "—" : current.LastBestEndpoint)}";
                Log($"LIVE SCAN: detected {current.DisplayName} ({current.ProcessName}).");
            }
            else if (current != null)
            {
                var refreshed = candidates.FirstOrDefault(x =>
                    x.ProcessName.Equals(current.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                    x.ExecutablePath.Equals(current.ExecutablePath, StringComparison.OrdinalIgnoreCase));
                if (refreshed != null) current = refreshed;
            }
            SyncEndpointFromMemory();
        }
        catch (Exception ex)
        {
            Log("[LIVE SCAN] " + ex.Message);
        }
        finally
        {
            liveDiscoveryBusy = false;
        }
    }

    void SyncEndpointFromMemory()
    {
        if (current == null || string.IsNullOrWhiteSpace(current.LastBestEndpoint)) return;
        var saved = current.LastBestEndpoint.Trim();
        if (saved.Length == 0) return;
        if (string.IsNullOrWhiteSpace(endpoint.Text) || !endpoint.Text.Contains(':', StringComparison.Ordinal))
            endpoint.Text = saved;
    }
}
