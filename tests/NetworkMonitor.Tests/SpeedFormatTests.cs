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

    [Theory]
    [InlineData(0, "0")]
    [InlineData(6, "0")]          // 48 bps
    [InlineData(7, "0.1K")]       // 56 bps
    [InlineData(125_000, "1.0M")] // 1 Mbps
    [InlineData(4_210_000, "34M")]
    [InlineData(125_000_000, "1.0G")]
    public void CompactInBits(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, SpeedFormat.Compact(bytesPerSecond, bits: true));

    [Theory]
    [InlineData(0, "0 bps")]
    [InlineData(100, "800 bps")]
    [InlineData(125, "1.00 kbps")]
    [InlineData(4_210_000, "33.7 Mbps")]
    [InlineData(125_000_000, "1.00 Gbps")]
    public void FullInBits(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, SpeedFormat.Full(bytesPerSecond, bits: true));

    public static IEnumerable<object[]> Sweep()
    {
        for (double v = 1; v < 1e13; v *= 1.07)
        {
            yield return [v, false];
            yield return [v, true];
        }
    }

    [Theory]
    [MemberData(nameof(Sweep))]
    public void CompactFitsTheIconAndIsIdleAgrees(double bytesPerSecond, bool bits)
    {
        string text = SpeedFormat.Compact(bytesPerSecond, bits);
        Assert.InRange(PixelFont.Measure(text), 1, TrayIconRenderer.Grid);
        Assert.Equal(text == "0", SpeedFormat.IsIdle(bytesPerSecond, bits));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(12_345_678, "12.3 MB")]
    [InlineData(1_240_000_000_000, "1.24 TB")]
    [InlineData(5_000_000_000_000_000, "5000 TB")]
    public void Amount(double bytes, string expected) => Assert.Equal(expected, SpeedFormat.Amount(bytes));
}
