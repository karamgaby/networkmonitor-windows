using System.IO.Pipes;
using System.Text;

namespace NetworkMonitor.Tests;

public class UsagePipeTests
{
    [Theory]
    [InlineData("{\"From\":\"2026-11-01\",\"To\":\"2026-11-16\"}", true)]
    [InlineData("{\"From\":\"2026-11-16\",\"To\":\"2026-11-16\"}", true)]
    [InlineData("{\"From\":\"2026-11-17\",\"To\":\"2026-11-16\"}", false)] // backwards
    [InlineData("{\"From\":\"2020-01-01\",\"To\":\"2026-11-16\"}", false)] // too many days
    [InlineData("{\"From\":\"yesterday\",\"To\":\"2026-11-16\"}", false)]
    [InlineData("null", false)]
    [InlineData("", false)]
    [InlineData("{", false)]
    public void ParsesOnlySaneRequests(string json, bool ok) =>
        Assert.Equal(ok, UsagePipe.Parse(Encoding.UTF8.GetBytes(json)) is not null);

    [Fact]
    public async Task AnswersRequestsAndSurvivesBadClients()
    {
        string name = "NetworkMonitor.Tests." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        UsagePipe.Request? asked = null;
        var server = UsagePipe.Serve(name, r =>
        {
            asked = r;
            var d = new DayUsage { CardDown = 42 };
            d.App("chrome").Down = 7;
            return d;
        }, stop.Token);

        // A client that sends garbage gets an error and the server carries on.
        using (var bad = new NamedPipeClientStream(".", name, PipeDirection.InOut))
        {
            await bad.ConnectAsync(2000);
            await bad.WriteAsync("not json\n"u8.ToArray());
            var reply = new StreamReader(bad).ReadLine();
            Assert.Contains("Error", reply);
        }

        var request = new UsagePipe.Request(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 16));
        var usage = await UsagePipe.Ask(name, request);
        Assert.NotNull(usage);
        Assert.Equal(42, usage.CardDown);
        Assert.Equal(7, usage.Apps["chrome"].Down);
        Assert.Equal(request, asked);

        // And again, on the same reused pipe instance.
        Assert.NotNull(await UsagePipe.Ask(name, request));

        stop.Cancel();
        await Stopped(server);
    }

    [Fact]
    public async Task NoServiceMeansNull() =>
        Assert.Null(await UsagePipe.Ask("NetworkMonitor.Tests.nobody." + Guid.NewGuid().ToString("N"),
            new UsagePipe.Request(new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 16)), timeoutMs: 300));

    [Fact]
    public async Task SecondServerOnTheSameNameFails()
    {
        string name = "NetworkMonitor.Tests." + Guid.NewGuid().ToString("N");
        using var stop = new CancellationTokenSource();
        var first = UsagePipe.Serve(name, _ => new DayUsage(), stop.Token);
        await Task.Delay(100);
        await Assert.ThrowsAnyAsync<Exception>(() => UsagePipe.Serve(name, _ => new DayUsage(), stop.Token));
        stop.Cancel();
        await Stopped(first);
    }

    /// <summary>Stopping ends the server, by returning or by cancelling the wait, within a second.</summary>
    static async Task Stopped(Task server)
    {
        try { await server.WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (OperationCanceledException) { }
    }
}
