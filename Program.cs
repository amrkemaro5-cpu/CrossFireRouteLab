using System;
using System.Windows.Forms;

namespace CrossFireRouteLab;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new ModernMainForm());
    }
}
