using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NetworkMonitor;

/// <summary>
/// <c>--install-service</c> / <c>--uninstall-service</c>. Asks for admin rights if it doesn't have them.
/// The service runs as LocalSystem, so it runs from a copy in Program Files: a copy in
/// %LOCALAPPDATA% could be swapped by any program running as you, and would then run as SYSTEM.
/// </summary>
internal static class Installer
{
    const int Cancelled = 1223; // ERROR_CANCELLED: No at the UAC prompt

    internal static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NetworkMonitor");

    internal static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>The process exit code: 0 when done, 1223 when UAC was declined.</summary>
    internal static int Run(bool install)
    {
        if (!IsAdmin) return RunElevated(install ? "--install-service" : "--uninstall-service");
        try
        {
            if (install) Install(); else Uninstall();
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show($"Couldn't {(install ? "install" : "remove")} the usage service:\n{e.Message}",
                "NetworkMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static int RunElevated(string argument)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, argument)
            {
                UseShellExecute = true,
                Verb = "runas",
            })!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            return Cancelled;
        }
    }

    static void Install()
    {
        Sc("stop", WindowsService.Name); // an update: the running copy locks the exe
        Directory.CreateDirectory(Folder);
        string source = AppContext.BaseDirectory;
        if (!string.Equals(Path.GetFullPath(source).TrimEnd('\\'), Folder, StringComparison.OrdinalIgnoreCase))
        {
            // shortcut: top-level files only, which is all a framework-dependent publish has; copy subfolders if that changes.
            foreach (string file in Directory.GetFiles(source))
                Retry(() => File.Copy(file, Path.Combine(Folder, Path.GetFileName(file)), overwrite: true));
        }
        SecureDataFolder();

        string exe = Path.Combine(Folder, "NetworkMonitor.exe");
        Expect(Sc("create", WindowsService.Name, "binPath=", $"\"{exe}\" --service", "start=", "auto",
                  "DisplayName=", "Network Monitor"), "create", 0, 1073 /* already exists */);
        Sc("description", WindowsService.Name, "Counts how much data each app uses, for the NetworkMonitor tray icon.");
        Sc("failure", WindowsService.Name, "reset=", "86400", "actions=", "restart/60000/restart/60000//");
        Expect(Sc("start", WindowsService.Name), "start", 0, 1056 /* already running */);
    }

    static void Uninstall()
    {
        Sc("stop", WindowsService.Name);
        Expect(Sc("delete", WindowsService.Name), "delete", 0, 1060 /* not installed */);
        if (Directory.Exists(Folder)) Retry(() => Directory.Delete(Folder, recursive: true));
        // The usage history in %ProgramData%\NetworkMonitor stays, for a reinstall.
    }

    /// <summary>
    /// The service writes here as SYSTEM, so no one else may own it or write to it. A folder that
    /// someone created beforehand, or a junction pointing elsewhere, is taken over or replaced.
    /// </summary>
    static void SecureDataFolder()
    {
        var dir = new DirectoryInfo(ServiceLog.DataFolder);
        if (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) dir.Delete();
        dir.Create();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(admins);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(admins,
            FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow)); // to read the log
        dir.SetAccessControl(security);
    }

    static int Sc(params string[] args)
    {
        var info = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (string a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode; // sc.exe exits with the Win32 error code
    }

    static void Expect(int code, string what, params int[] ok)
    {
        if (!ok.Contains(code)) throw new Win32Exception(code, $"sc {what}: {new Win32Exception(code).Message} ({code})");
    }

    /// <summary>A stopped service's process can take a moment to exit and release its files.</summary>
    static void Retry(Action action)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (i < 20 && e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}
