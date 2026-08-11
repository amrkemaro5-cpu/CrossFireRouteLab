namespace CrossFireRouteLab;

public sealed partial class DashboardForm
{
    void AddAction(Control parent, GRLIcon icon, string title, Func<Task> action, Color accent)
    {
        var button = new GRLActionButton
        {
            Text = title,
            Icon = icon,
            Accent = accent,
            Width = 106,
            Height = 76,
            Margin = new Padding(3, 0, 3, 0)
        };
        button.Click += async (_, _) => await Safe(action);
        actions.Add(button);
        parent.Controls.Add(button);
    }

    // The reference layout is already expressed directly by the dashboard controls.
    // Keep this hook so older startup code remains source-compatible without applying
    // a second layout pass that could shift controls away from their intended anchors.
    void ApplyReferenceLayout()
    {
        PerformLayout();
    }
}

enum GRLIcon
{
    Radar,
    Gamepad,
    Network,
    Router,
    Search,
    Route,
    Ping,
    Trace,
    Chart,
    Report,
    Trash
}
