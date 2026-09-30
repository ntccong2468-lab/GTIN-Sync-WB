using MarketplaceHub.Services;
using MarketplaceHub.UI;

namespace MarketplaceHub;

internal static class Program
{
    private static string CrashReportPath =>
        Path.Combine(Path.GetTempPath(), "MarketplaceHub-gui-crash.txt");

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
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => WriteCrash("ThreadException", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash("UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown"));

        try
        {
            if (File.Exists(CrashReportPath)) File.Delete(CrashReportPath);
            Application.Run(new MainForm(new AppServices()));
        }
        catch (Exception ex)
        {
            WriteCrash("Main", ex);
            Environment.ExitCode = 10;
        }
    }

    private static void WriteCrash(string source, Exception ex)
    {
        try
        {
            File.AppendAllText(
                CrashReportPath,
                $"[{DateTimeOffset.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
        }
        catch { }
    }
}
