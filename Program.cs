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
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 38, BackColor = Color.FromArgb(2, 6, 15) };
        footer.Controls.Add(new Label
        {
            Text = "▣  System: Windows 64-bit",
            ForeColor = Color.FromArgb(0, 224, 255),
            AutoSize = true,
            Location = new Point(290, 10),
            Font = new Font("Segoe UI", 9.5f)
        });
        footer.Controls.Add(new Label
        {
            Text = "◷  Game Route Lab v5.0    •    READ-ONLY MODE",
            ForeColor = Color.FromArgb(40, 235, 110),
            AutoSize = true,
            Location = new Point(20, 10),
            Font = new Font("Segoe UI Semibold", 9.5f)
        });
        footer.Controls.Add(new Label
        {
            Text = "●  READY",
            ForeColor = Color.FromArgb(40, 235, 110),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(form.ClientSize.Width - 105, 10),
            Font = new Font("Segoe UI Semibold", 9.5f)
        });
        form.Controls.Add(footer);
        Application.Run(form);
    }
}
