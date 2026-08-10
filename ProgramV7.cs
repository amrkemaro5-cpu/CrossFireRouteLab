using System.Windows.Forms;

namespace CrossFireRouteLab;

internal static class ProgramV7
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new GameRouteLabDashboard());
    }
}
