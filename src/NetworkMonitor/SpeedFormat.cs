using System.Globalization;

namespace NetworkMonitor;

/// <summary>Bytes per second as text, in decimal units (1 KB = 1000 bytes), like network speeds.</summary>
internal static class SpeedFormat
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Below this, Compact shows "0".
    const double IdleBelow = 50;

    internal static bool IsIdle(double bytesPerSecond) => bytesPerSecond < IdleBelow;

    /// <summary>At most 4 characters for the 16 px tray icon: "0", "0.3K", "850K", "4.2M", "120M".</summary>
    internal static string Compact(double bytesPerSecond)
    {
        if (IsIdle(bytesPerSecond)) return "0";
        string[] units = ["K", "M", "G"];
        double v = bytesPerSecond / 1000;
        int u = 0;
        while (v >= 999.5 && u < units.Length - 1) { v /= 1000; u++; }
        string number = v < 9.95 ? v.ToString("0.0", Inv) : Math.Min(v, 999).ToString("0", Inv);
        return number + units[u];
    }

    /// <summary>Three significant digits for the tooltip: "999 B/s", "12.3 KB/s", "4.21 MB/s".</summary>
    internal static string Full(double bytesPerSecond)
    {
        if (bytesPerSecond < 999.5) return $"{bytesPerSecond.ToString("0", Inv)} B/s";
        string[] units = ["KB/s", "MB/s", "GB/s"];
        double v = bytesPerSecond / 1000;
        int u = 0;
        while (v >= 999.5 && u < units.Length - 1) { v /= 1000; u++; }
        string number = v < 9.995 ? v.ToString("0.00", Inv)
            : v < 99.95 ? v.ToString("0.0", Inv)
            : v.ToString("0", Inv);
        return $"{number} {units[u]}";
    }
}
