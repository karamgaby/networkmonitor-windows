using System.Net;

namespace NetworkMonitor.Tests;

public class AddressScopeTests
{
    [Theory]
    [InlineData("127.0.0.1", "Loopback")]
    [InlineData("127.12.0.9", "Loopback")]
    [InlineData("::1", "Loopback")]
    [InlineData("::ffff:127.0.0.1", "Loopback")]
    [InlineData("10.1.2.3", "Lan")]
    [InlineData("172.16.0.1", "Lan")]
    [InlineData("172.31.255.255", "Lan")]
    [InlineData("192.168.1.1", "Lan")]
    [InlineData("169.254.10.10", "Lan")]
    [InlineData("100.64.0.1", "Lan")]
    [InlineData("100.116.63.80", "Lan")]      // Tailscale
    [InlineData("224.0.0.251", "Lan")]        // mDNS
    [InlineData("255.255.255.255", "Lan")]
    [InlineData("0.0.0.0", "Lan")]
    [InlineData("::", "Lan")]
    [InlineData("::ffff:192.168.0.5", "Lan")]
    [InlineData("fe80::1", "Lan")]
    [InlineData("febf::1", "Lan")]
    [InlineData("fd7a:115c:a1e0::1", "Lan")]  // Tailscale IPv6
    [InlineData("fc00::1", "Lan")]
    [InlineData("ff02::fb", "Lan")]
    [InlineData("8.8.8.8", "Internet")]
    [InlineData("172.32.0.1", "Internet")]
    [InlineData("172.15.0.1", "Internet")]
    [InlineData("100.128.0.1", "Internet")]
    [InlineData("100.63.0.1", "Internet")]
    [InlineData("192.169.0.1", "Internet")]
    [InlineData("::ffff:1.1.1.1", "Internet")]
    [InlineData("2606:4700:4700::1111", "Internet")]
    [InlineData("fec0::1", "Lan")]            // site-local, deprecated but private
    [InlineData("fe7f::1", "Internet")]
    public void Classifies(string address, string expected) =>
        Assert.Equal(Enum.Parse<Scope>(expected), AddressScope.Classify(IPAddress.Parse(address).GetAddressBytes()));

    [Fact]
    public void OddLengthIsInternet() => Assert.Equal(Scope.Internet, AddressScope.Classify(new byte[5]));
}
