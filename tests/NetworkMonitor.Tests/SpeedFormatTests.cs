namespace NetworkMonitor.Tests;

public class SpeedFormatTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(49, "0")]
    [InlineData(50, "0.1K")]
    [InlineData(9_940, "9.9K")]
    [InlineData(9_960, "10K")]
    [InlineData(850_000, "850K")]
    [InlineData(999_400, "999K")]
    [InlineData(999_600, "1.0M")]
    [InlineData(4_210_000, "4.2M")]
    [InlineData(120_000_000, "120M")]
    [InlineData(2_500_000_000, "2.5G")]
    [InlineData(5e12, "999G")]
    public void Compact(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, SpeedFormat.Compact(bytesPerSecond));

    [Theory]
    [InlineData(0, "0 B/s")]
    [InlineData(999, "999 B/s")]
    [InlineData(1_000, "1.00 KB/s")]
    [InlineData(12_340, "12.3 KB/s")]
    [InlineData(850_000, "850 KB/s")]
    [InlineData(4_210_000, "4.21 MB/s")]
    [InlineData(1_250_000_000, "1.25 GB/s")]
    public void Full(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, SpeedFormat.Full(bytesPerSecond));

    public static IEnumerable<object[]> Sweep()
    {
        for (double v = 1; v < 1e13; v *= 1.07) yield return [v];
    }

    [Theory]
    [MemberData(nameof(Sweep))]
    public void CompactFitsTheIconAndIsIdleAgrees(double bytesPerSecond)
    {
        string text = SpeedFormat.Compact(bytesPerSecond);
        Assert.InRange(PixelFont.Measure(text), 1, TrayIconRenderer.Grid);
        Assert.Equal(text == "0", SpeedFormat.IsIdle(bytesPerSecond));
    }
}
