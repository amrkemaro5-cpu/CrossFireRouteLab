using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace CrossFireRouteLab;

/// <summary>
/// Prevents the legacy generic analysis buttons from taking over while CrossFire is running.
/// The unified CrossFireRouteEngine runs continuously; these buttons become safe status actions.
/// </summary>
internal static class CrossFireUiGuard
{
    static bool armed;
    static readonly Dictionary<string, Delegate?> originals = new(StringComparer.OrdinalIgnoreCase);

    public static void Apply(GameRouteLabV10Form form)
    {
        if (armed || form.IsDisposed) return;
        armed = true;
        foreach (var name in new[] { "AUTO ANALYZE", "FIND CONNECTIONS", "PING 30x", "PATH QUALITY" }) Install(form, name);
    }

    static void Install(GameRouteLabV10Form form, string text)
    {
        var button = AllControls(form).OfType<Button>().FirstOrDefault(x => x.Text.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (button == null) return;
        try
        {
            var eventsProperty = typeof(Component).GetProperty("Events", BindingFlags.Instance | BindingFlags.NonPublic);
            var list = eventsProperty?.GetValue(button) as EventHandlerList;
            var clickKey = typeof(Control).GetField("EventClick", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
            if (list == null || clickKey == null) return;
            originals[text] = list[clickKey];
            list[clickKey] = null;
            button.Click += (_, e) =>
            {
                if (IsCrossFireRunning())
                {
                    form.BeginInvoke((Action)(() => typeof(GameRouteLabV10Form).GetMethod("Log", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(form, new object[] { $"[CROSSFIRE] {text} is controlled by the unified CrossFire route engine. Live TCP candidates and route tests update automatically." })));
                    return;
                }
                try { originals[text]?.DynamicInvoke(button, e); } catch { }
            };
        }
        catch { }
    }

    static bool IsCrossFireRunning()
    {
        try { return Process.GetProcesses().Any(p => { try { return p.ProcessName.Contains("crossfire", StringComparison.OrdinalIgnoreCase); } catch { return false; } finally { p.Dispose(); } }); }
        catch { return false; }
    }

    static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in AllControls(child)) yield return nested;
        }
    }
}
