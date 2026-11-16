using System.Runtime.InteropServices;

namespace NetworkMonitor.Tests;

public class InterfaceCountersTests
{
    [Fact]
    public void RowMatchesNetioapiLayout()
    {
        // Offsets of MIB_IF_ROW2 on x64 and x86 (it has no pointers, so both are the same).
        Assert.Equal(1352, Marshal.SizeOf<MibIfRow2>());
        Assert.Equal(1152, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InterfaceAndOperStatusFlags)));
        Assert.Equal(1156, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.OperStatus)));
        Assert.Equal(1208, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.InOctets)));
        Assert.Equal(1280, (int)Marshal.OffsetOf<MibIfRow2>(nameof(MibIfRow2.OutOctets)));
    }

    [Theory]
    [InlineData(0x01, 1, true)]   // physical NIC, up
    [InlineData(0x03, 1, false)]  // its NDIS filter instance (WFP, QoS): same traffic again
    [InlineData(0x00, 1, false)]  // loopback, VPN tunnel, vEthernet
    [InlineData(0x01, 2, false)]  // physical NIC, down
    [InlineData(0x81, 1, true)]   // other flag bits don't matter
    public void CountsUpPhysicalNicsOnly(byte flags, uint operStatus, bool counted) =>
        Assert.Equal(counted, InterfaceCounters.IsCounted(flags, operStatus));

    [Fact]
    public void ReadsThisMachine()
    {
        var rows = InterfaceCounters.Read();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.NotEqual(0ul, r.Luid));
        var loopback = Assert.Single(rows, r => r.Description.Contains("Loopback", StringComparison.OrdinalIgnoreCase));
        Assert.False(loopback.Counted);
    }
}
