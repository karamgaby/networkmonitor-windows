using System.Drawing;

namespace NetworkMonitor.Tests;

public class TrayIconRendererTests
{
    static readonly Color Down = Color.FromArgb(255, 0x51, 0xFF, 0x70);
    static readonly Color Up = Color.FromArgb(255, 0xFF, 0xB4, 0x54);

    [Fact]
    public void GlyphsAreWellFormed()
    {
        foreach (char c in PixelFont.Characters)
        {
            var g = PixelFont.Get(c);
            Assert.Equal(g.Width * PixelFont.Height, g.Bits.Length);
            Assert.Contains('1', g.Bits);
        }
    }

    [Fact]
    public void DownloadOnTopUploadBelow()
    {
        using var bitmap = TrayIconRenderer.Render("850K", Down, "4.2M", Up, 16);
        Assert.Equal(new Size(16, 16), bitmap.Size);
        var (down, up) = Count(bitmap);
        Assert.True(down.Top > 0 && down.Bottom == 0, "download pixels belong on the top row only");
        Assert.True(up.Bottom > 0 && up.Top == 0, "upload pixels belong on the bottom row only");
    }

    [Fact]
    public void ScalesByWholePixels()
    {
        using var small = TrayIconRenderer.Render("999K", Down, "9.9M", Up, 16);
        using var large = TrayIconRenderer.Render("999K", Down, "9.9M", Up, 32);
        var (d16, u16) = Count(small);
        var (d32, u32) = Count(large);
        Assert.Equal(4 * (d16.Top + d16.Bottom), d32.Top + d32.Bottom);
        Assert.Equal(4 * (u16.Top + u16.Bottom), u32.Top + u32.Bottom);
    }

    [Fact]
    public void UnknownCharacterIsRejected() =>
        Assert.Throws<ArgumentException>(() => PixelFont.Measure("1x"));

    static ((int Top, int Bottom) Down, (int Top, int Bottom) Up) Count(Bitmap b)
    {
        int dTop = 0, dBottom = 0, uTop = 0, uBottom = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                int argb = b.GetPixel(x, y).ToArgb();
                bool top = y < b.Height / 2;
                if (argb == Down.ToArgb()) { if (top) dTop++; else dBottom++; }
                else if (argb == Up.ToArgb()) { if (top) uTop++; else uBottom++; }
                else Assert.Equal(0, argb >> 24); // nothing but the two colours and transparency
            }
        return ((dTop, dBottom), (uTop, uBottom));
    }
}
