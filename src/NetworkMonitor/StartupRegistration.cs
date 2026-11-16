using Microsoft.Win32;

namespace NetworkMonitor;

/// <summary>Start at login through the per-user Run key. No admin rights needed.</summary>
internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "NetworkMonitor";

    static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>On only when it starts this copy: an entry left by a copy that moved counts as off.</summary>
    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue(ValueName) as string, Command, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
