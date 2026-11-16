using System.Drawing.Imaging;

namespace NetworkMonitor;

/// <summary>Draws the tray icon: download on the top row, upload on the bottom.</summary>
internal static class TrayIconRenderer
{
    internal const int Grid = 16;
    internal const int TopRow = 2, BottomRow = 9;

    /// <summary>
    /// Draws on a 16-pixel grid, scaled up by whole pixels for larger icon sizes (at 200%,
    /// for example). Scaling by a fraction would blur the pixel font.
    /// </summary>
    internal static Bitmap Render(string down, Color downColor, string up, Color upColor, int size)
    {
        int scale = Math.Max(1, size / Grid);
        int offset = (size - Grid * scale) / 2;
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        DrawRow(g, down, downColor, TopRow, scale, offset);
        DrawRow(g, up, upColor, BottomRow, scale, offset);
        return bitmap;
    }

    static void DrawRow(Graphics g, string text, Color color, int top, int scale, int offset)
    {
        using var brush = new SolidBrush(color);
        int x = (Grid - PixelFont.Measure(text)) / 2;
        foreach (char c in text)
        {
            var glyph = PixelFont.Get(c);
            for (int row = 0; row < PixelFont.Height; row++)
                for (int col = 0; col < glyph.Width; col++)
                    if (glyph.On(col, row))
                        g.FillRectangle(brush, offset + (x + col) * scale, offset + (top + row) * scale, scale, scale);
            x += glyph.Width + 1;
        }
    }
}
