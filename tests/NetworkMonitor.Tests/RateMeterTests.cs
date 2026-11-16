namespace NetworkMonitor.Tests;

public class RateMeterTests
{
    static InterfaceSample Nic(ulong luid, ulong received, ulong sent) =>
        new(luid, $"nic{luid}", "", true, received, sent);

    [Fact]
    public void FirstSampleIsZero()
    {
        var meter = new RateMeter();
        Assert.Equal((0d, 0d), meter.Update([Nic(1, 5_000, 1_000)], 1));
    }

    [Fact]
    public void RateIsBytesOverSeconds()
    {
        var meter = new RateMeter();
        meter.Update([Nic(1, 1_000, 500)], 1);
        Assert.Equal((2_000d, 500d), meter.Update([Nic(1, 5_000, 1_500)], 2));
    }

    [Fact]
    public void InterfacesAddUp()
    {
        var meter = new RateMeter();
        meter.Update([Nic(1, 0, 0), Nic(2, 0, 0)], 1);
        Assert.Equal((300d, 30d), meter.Update([Nic(1, 100, 10), Nic(2, 200, 20)], 1));
    }

    [Fact]
    public void CounterGoingBackwardsAddsNothingThenResumes()
    {
        var meter = new RateMeter();
        meter.Update([Nic(1, 10_000, 10_000)], 1);
        Assert.Equal((0d, 0d), meter.Update([Nic(1, 100, 100)], 1)); // driver reset
        Assert.Equal((900d, 900d), meter.Update([Nic(1, 1_000, 1_000)], 1));
    }

    [Fact]
    public void NewInterfaceWaitsForItsBaseline()
    {
        var meter = new RateMeter();
        meter.Update([Nic(1, 0, 0)], 1);
        // Ethernet plugged in with a counter that's already high: it must not show up as a burst.
        Assert.Equal((100d, 0d), meter.Update([Nic(1, 100, 0), Nic(2, 9_000_000, 9_000_000)], 1));
        Assert.Equal((150d, 50d), meter.Update([Nic(1, 200, 0), Nic(2, 9_000_050, 9_000_050)], 1));
    }

    [Fact]
    public void RemovedInterfaceAddsNothing()
    {
        var meter = new RateMeter();
        meter.Update([Nic(1, 0, 0), Nic(2, 0, 0)], 1);
        Assert.Equal((100d, 0d), meter.Update([Nic(1, 100, 0)], 1));
    }

    [Fact]
    public void ZeroSecondsIsZeroNotInfinity()
    {
        var meter = new RateMeter();
        meter.Update([Nic(1, 0, 0)], 1);
        Assert.Equal((0d, 0d), meter.Update([Nic(1, 100, 100)], 0));
    }
}
