using System.Globalization;

namespace NetworkMonitor;

/// <summary>
/// Speed as text, in decimal units (1 K = 1000) like network speeds: bytes per second by
/// default, or bits per second when <c>bits</c> is set.
/// </summary>
internal static class SpeedFormat
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Below this, in the unit shown, Compact shows "0".
    const double IdleBelow = 50;

    static double InUnit(double bytesPerSecond, bool bits) => bits ? bytesPerSecond * 8 : bytesPerSecond;

    internal static bool IsIdle(double bytesPerSecond, bool bits = false) => InUnit(bytesPerSecond, bits) < IdleBelow;

    /// <summary>
    /// At most 4 characters, to fit the 16 px tray icon: "0", "0.3K", "850K", "4.2M", "120M".
    /// The pixel font has no room for B or b, so the tooltip says which unit is in use.
    /// </summary>
    internal static string Compact(double bytesPerSecond, bool bits = false)
    {
        if (IsIdle(bytesPerSecond, bits)) return "0";
        string[] units = ["K", "M", "G"];
        double v = InUnit(bytesPerSecond, bits) / 1000;
        int u = 0;
        while (v >= 999.5 && u < units.Length - 1) { v /= 1000; u++; }
        string number = v < 9.95 ? v.ToString("0.0", Inv) : Math.Min(v, 999).ToString("0", Inv);
        return number + units[u];
    }

    /// <summary>Three significant digits for the tooltip: "999 B/s", "12.3 KB/s", "33.7 Mbps".</summary>
    internal static string Full(double bytesPerSecond, bool bits = false)
    {
        string[] units = bits ? ["bps", "kbps", "Mbps", "Gbps"] : ["B/s", "KB/s", "MB/s", "GB/s"];
        double v = InUnit(bytesPerSecond, bits);
        if (v < 999.5) return $"{v.ToString("0", Inv)} {units[0]}";
        v /= 1000;
        int u = 1;
        while (v >= 999.5 && u < units.Length - 1) { v /= 1000; u++; }
        string number = v < 9.995 ? v.ToString("0.00", Inv)
            : v < 99.95 ? v.ToString("0.0", Inv)
            : v.ToString("0", Inv);
        return $"{number} {units[u]}";
    }
}
