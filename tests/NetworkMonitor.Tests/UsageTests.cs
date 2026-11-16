namespace NetworkMonitor.Tests;

public sealed class UsageTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "NetworkMonitor.Tests", Guid.NewGuid().ToString("N"));
    static readonly DateOnly Monday = new(2026, 11, 16);
    const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AddSplitsDirectionAndScope()
    {
        var a = new AppUsage();
        a.Add(lan: false, send: false, 1);
        a.Add(lan: false, send: true, 10);
        a.Add(lan: true, send: false, 100);
        a.Add(lan: true, send: true, 1000);
        Assert.Equal((1, 10, 100, 1000, 1111), (a.Down, a.Up, a.LanDown, a.LanUp, a.Total));
    }

    [Fact]
    public void NothingCountsUntilFlushed()
    {
        var book = new UsageBook(_dir, Monday);
        book.Add(Chrome, lan: false, send: false, 500);
        Assert.Empty(book.Read(Monday, Monday).Apps);
        book.Flush(Monday, 600, 50);
        var day = book.Read(Monday, Monday);
        Assert.Equal(500, day.Apps[Chrome].Down);
        Assert.Equal((600, 50), (day.CardDown, day.CardUp));
    }

    [Fact]
    public void FlushesAddUp()
    {
        var book = new UsageBook(_dir, Monday);
        book.Add(Chrome, false, false, 500);
        book.Flush(Monday, 0, 0);
        book.Add(Chrome, false, false, 250);
        book.Add(DayUsage.SystemApp, true, true, 7);
        book.Flush(Monday, 0, 0);
        var day = book.Read(Monday, Monday);
        Assert.Equal(750, day.Apps[Chrome].Down);
        Assert.Equal(7, day.Apps[DayUsage.SystemApp].LanUp);
    }

    [Fact]
    public void NewDaySavesTheOldOneAndStartsEmpty()
    {
        var book = new UsageBook(_dir, Monday);
        book.Add(Chrome, false, false, 500);
        book.Flush(Monday, 1000, 0);
        book.Add(Chrome, false, false, 30);
        book.Flush(Monday.AddDays(1), 40, 0); // the first flush after midnight

        Assert.Equal(500, DayUsage.TryLoad(DayUsage.PathFor(_dir, Monday))!.Apps[Chrome].Down);
        Assert.Equal(30, book.Read(Monday.AddDays(1), Monday.AddDays(1)).Apps[Chrome].Down);
        var both = book.Read(Monday, Monday.AddDays(1));
        Assert.Equal((530, 1040), (both.Apps[Chrome].Down, both.CardDown));
    }

    [Fact]
    public void RestartingTheSameDayContinuesItsTotals()
    {
        var book = new UsageBook(_dir, Monday);
        book.Add(Chrome, false, true, 9);
        book.Flush(Monday, 0, 0);
        book.Save();
        var again = new UsageBook(_dir, Monday);
        again.Add(Chrome, false, true, 1);
        again.Flush(Monday, 0, 0);
        Assert.Equal(10, again.Read(Monday, Monday).Apps[Chrome].Up);
        Assert.False(File.Exists(DayUsage.PathFor(_dir, Monday) + ".tmp"));
    }

    [Fact]
    public void MissingDaysAreEmpty()
    {
        var book = new UsageBook(_dir, Monday);
        var month = book.Read(new DateOnly(2026, 11, 1), Monday);
        Assert.Empty(month.Apps);
        Assert.Equal(0, month.CardDown);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{\"Apps\": null}")]
    [InlineData("{\"Apps\": {\"x\": null}}")]
    [InlineData("{\"CardDown\": \"lots\"}")]
    public void DamagedDayIsKeptAsideNotOverwritten(string content)
    {
        Directory.CreateDirectory(_dir);
        string path = DayUsage.PathFor(_dir, Monday);
        File.WriteAllText(path, content);
        Assert.Null(DayUsage.TryLoad(path));

        var book = new UsageBook(_dir, Monday);
        Assert.Equal(content, File.ReadAllText(path + ".damaged"));
        book.Add(Chrome, false, false, 1);
        book.Flush(Monday, 0, 0);
        book.Save();
        Assert.Equal(1, DayUsage.TryLoad(path)!.Apps[Chrome].Down);
    }

    [Fact]
    public void DamagedPastDayReadsAsNothing()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(DayUsage.PathFor(_dir, Monday.AddDays(-1)), "garbage");
        var book = new UsageBook(_dir, Monday);
        Assert.Empty(book.Read(Monday.AddDays(-1), Monday).Apps);
    }

    [Fact]
    public void ConcurrentAddsAreAllCounted()
    {
        var book = new UsageBook(_dir, Monday);
        Parallel.For(0, 8, _ => { for (int i = 0; i < 10_000; i++) book.Add(Chrome, false, false, 1); });
        book.Flush(Monday, 0, 0);
        Assert.Equal(80_000, book.Read(Monday, Monday).Apps[Chrome].Down);
    }
}
