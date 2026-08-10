using System;
using System.Drawing;
using System.Windows.Forms;

namespace CrossFireRouteLab;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var form = new ModernMainForm();
        FixQuickActions(form);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Color.FromArgb(2, 6, 15) };
        footer.Controls.Add(new Label { Text = "▣  System: Windows 64-bit", ForeColor = Color.FromArgb(0,224,255), AutoSize = true, Location = new Point(290,10), Font = new Font("Segoe UI",9.5f) });
        footer.Controls.Add(new Label { Text = "◷  Game Route Lab v5.0    •    READ-ONLY MODE", ForeColor = Color.FromArgb(40,235,110), AutoSize = true, Location = new Point(20,10), Font = new Font("Segoe UI Semibold",9.5f) });
        var ready = new Label { Text = "●  READY", ForeColor = Color.FromArgb(40,235,110), AutoSize = true, Anchor = AnchorStyles.Top|AnchorStyles.Right, Font = new Font("Segoe UI Semibold",9.5f) };
        footer.Controls.Add(ready);
        footer.Resize += (_,_) => ready.Location = new Point(footer.ClientSize.Width - ready.Width - 20, 10);
        form.Controls.Add(footer);
        Application.Run(form);
    }

    static void FixQuickActions(Control root)
    {
        foreach(Control c in root.Controls)
        {
            if(c is Label l && l.Text == "QUICK ACTIONS" && c.Parent is Panel p)
            {
                p.Dock = DockStyle.None;
                p.Location = new Point(0, 700);
                p.Size = new Size(275, 210);
                p.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            }
            if(c.HasChildren) FixQuickActions(c);
        }
    }
}
