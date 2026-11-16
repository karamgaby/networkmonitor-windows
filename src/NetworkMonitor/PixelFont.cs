namespace NetworkMonitor;

/// <summary>
/// A 3×5 pixel font for the tray icon. At 16×16, a real font blurs into mush. Exact pixels
/// fit two rows of four characters, and they stay sharp.
/// </summary>
internal static class PixelFont
{
    internal const int Height = 5;

    internal readonly record struct Glyph(int Width, string Bits)
    {
        public bool On(int col, int row) => Bits[row * Width + col] == '1';
    }

    static readonly Dictionary<char, Glyph> Glyphs = new()
    {
        ['0'] = new(3, "111" + "101" + "101" + "101" + "111"),
        ['1'] = new(3, "010" + "110" + "010" + "010" + "111"),
        ['2'] = new(3, "111" + "001" + "111" + "100" + "111"),
        ['3'] = new(3, "111" + "001" + "111" + "001" + "111"),
        ['4'] = new(3, "101" + "101" + "111" + "001" + "001"),
        ['5'] = new(3, "111" + "100" + "111" + "001" + "111"),
        ['6'] = new(3, "111" + "100" + "111" + "101" + "111"),
        ['7'] = new(3, "111" + "001" + "001" + "001" + "001"),
        ['8'] = new(3, "111" + "101" + "111" + "101" + "111"),
        ['9'] = new(3, "111" + "101" + "111" + "001" + "111"),
        ['K'] = new(3, "101" + "101" + "110" + "101" + "101"),
        ['M'] = new(3, "101" + "111" + "111" + "101" + "101"),
        ['G'] = new(3, "011" + "100" + "101" + "101" + "011"),
        ['.'] = new(1, "0" + "0" + "0" + "0" + "1"),
    };

    internal static IEnumerable<char> Characters => Glyphs.Keys;

    internal static Glyph Get(char c) =>
        Glyphs.TryGetValue(c, out var g) ? g : throw new ArgumentException($"No glyph for '{c}'", nameof(c));

    /// <summary>Width in pixels, with one blank column between characters.</summary>
    internal static int Measure(string text) =>
        text.Sum(c => Get(c).Width) + Math.Max(0, text.Length - 1);
}
