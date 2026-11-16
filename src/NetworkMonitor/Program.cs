namespace NetworkMonitor;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        switch (args)
        {
            case ["--service"]: return WindowsService.Run(() => new UsageCollector());
            case ["--install-service"]: return Installer.Run(install: true);
            case ["--uninstall-service"]: return Installer.Run(install: false);
        }

        // One tray icon is enough: a second launch (Start at login + a manual run) just exits.
        using var mutex = new Mutex(true, @"Local\NetworkMonitor.Windows", out bool first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        using var app = new TrayApp();
        Application.Run(app);
        return 0;
    }
}
