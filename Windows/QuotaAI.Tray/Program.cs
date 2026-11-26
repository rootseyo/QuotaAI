namespace QuotaAI.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(initiallyOwned: false, @"Local\QuotaAI.Tray", out var created);
        if (!created)
            return;

        ApplicationConfiguration.Initialize();
        using var app = new TrayApplication();
        Application.Run(app);
    }
}
