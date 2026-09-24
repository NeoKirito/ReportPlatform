using System.Windows.Forms;

namespace PEIS.PrintAgent.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var config = AgentInstallerService.ResolveConfiguration(args);

        var isSilent = args.Any(a =>
            string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/s", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "-s", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/silent", StringComparison.OrdinalIgnoreCase));

        if (isSilent)
        {
            try
            {
                AgentInstallerService.ExecuteInstall(config);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] Agent installation failed: {ex.Message}");
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(config));
        return 0;
    }
}
