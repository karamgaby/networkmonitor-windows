using System.Drawing;

namespace NetworkMonitor.Tests;

public sealed class SettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "NetworkMonitor.Tests", Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void MissingFileMeansDefaults() => Assert.Equal(new Settings(), Settings.Load(FilePath));

    [Fact]
    public void SavesAndLoadsBack()
    {
        var saved = new Settings(Bits: true, DownColor: "#112233", UpColor: null);
        saved.Save(FilePath); // creates the folder too
        Assert.Equal(saved, Settings.Load(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{\"Bits\": \"yes\"}")]
    public void DamagedFileMeansDefaults(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, content);
        Assert.Equal(new Settings(), Settings.Load(FilePath));
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{\"Bits\": true, \"FromANewerVersion\": 1}");
        Assert.True(Settings.Load(FilePath).Bits);
    }

    [Theory]
    [InlineData("#51FF70", 0x51, 0xFF, 0x70)]
    [InlineData("#a85200", 0xA8, 0x52, 0x00)]
    public void ParsesColours(string hex, int r, int g, int b) =>
        Assert.Equal(Color.FromArgb(255, r, g, b), Settings.ParseColor(hex));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("51FF70")]
    [InlineData("#51FF7")]
    [InlineData("#GGGGGG")]
    [InlineData("#-1FF70")]
    public void BadColoursMeanTheThemeColour(string? hex) => Assert.Null(Settings.ParseColor(hex));

    [Fact]
    public void ColourRoundTrips() =>
        Assert.Equal(Color.FromArgb(255, 1, 2, 254), Settings.ParseColor(Settings.ToHex(Color.FromArgb(1, 2, 254))));

    [Fact]
    public void PickedColoursReplaceTheThemeInBothModes()
    {
        var settings = new Settings(DownColor: "#123456");
        foreach (bool light in new[] { true, false })
        {
            var p = Palette.For(light, settings);
            Assert.Equal(Color.FromArgb(255, 0x12, 0x34, 0x56), p.Down);
            Assert.Equal(Palette.For(light, new Settings()).Up, p.Up); // the other one keeps the theme
        }
    }
}
