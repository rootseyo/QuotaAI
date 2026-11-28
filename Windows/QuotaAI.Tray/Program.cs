using System.Diagnostics;

namespace QuotaAI.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // After a self-update the old process is still shutting down; wait for it
        // so the single-instance lock below is free.
        if (args is ["--restart-after", var pidText] && int.TryParse(pidText, out var pid))
        {
            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(15_000);
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        using var instance = new Mutex(initiallyOwned: false, @"Local\QuotaAI.Tray", out var created);
        if (!created)
            return;

        SelfUpdater.CleanupLeftovers();
        ApplicationConfiguration.Initialize();
        using var app = new TrayApplication();
        Application.Run(app);
    }
}
