namespace NetworkMonitor;

static class Program
{
    [STAThread]
    static void Main()
    {
        // One tray icon is enough: a second launch (Start at login + a manual run) just exits.
        using var mutex = new Mutex(true, @"Local\NetworkMonitor.Windows", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        using var app = new TrayApp();
        Application.Run(app);
    }
}
