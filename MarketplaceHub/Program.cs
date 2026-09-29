using MarketplaceHub.Services;
using MarketplaceHub.UI;

namespace MarketplaceHub;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            try { Environment.Exit(AppServices.SelfTest() ? 0 : 2); }
            catch { Environment.Exit(3); }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(new AppServices()));
    }
}
