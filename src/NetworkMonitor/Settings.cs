using System.Globalization;
using System.Text.Json;

namespace NetworkMonitor;

/// <summary>What you chose in the tray menu, kept in %APPDATA%\NetworkMonitor\settings.json.</summary>
/// <param name="Bits">Show Mbps instead of MB/s.</param>
/// <param name="DownColor">"#RRGGBB", or null for the taskbar theme's colour.</param>
/// <param name="UpColor">"#RRGGBB", or null for the taskbar theme's colour.</param>
internal sealed record Settings(bool Bits = false, string? DownColor = null, string? UpColor = null)
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetworkMonitor", "settings.json");

    /// <summary>A missing, unreadable or damaged file means the defaults. Settings never stop the app from starting.</summary>
    internal static Settings Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Indented));
        File.Move(temp, path, overwrite: true); // a crash mid-write leaves the old file whole
    }

    internal static Color? ParseColor(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#'
        && int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb)
            ? Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF)
            : null;

    internal static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
