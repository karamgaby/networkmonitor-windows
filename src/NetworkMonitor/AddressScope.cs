namespace NetworkMonitor;

internal enum Scope { Internet, Lan, Loopback }

/// <summary>Where a remote address is: this PC, the home network, or the internet.</summary>
internal static class AddressScope
{
    /// <param name="a">A 4-byte IPv4 or 16-byte IPv6 address, in network byte order, as ETW gives it.</param>
    internal static Scope Classify(ReadOnlySpan<byte> a)
    {
        if (a.Length == 16)
        {
            if (a[..10].IndexOfAnyExcept((byte)0) < 0 && a[10] == 0xFF && a[11] == 0xFF)
                return Classify(a[12..]);                                 // ::ffff:a.b.c.d
            if (a[..15].IndexOfAnyExcept((byte)0) < 0)
                return a[15] == 1 ? Scope.Loopback : Scope.Lan;           // ::1, ::
            return (a[0] & 0xFE) == 0xFC                                  // fc00::/7 unique local, Tailscale's fd7a:115c:a1e0::/48 too
                || (a[0] == 0xFE && (a[1] & 0x80) == 0x80)                // fe80::/10 link-local, fec0::/10 site-local
                || a[0] == 0xFF                                           // multicast
                ? Scope.Lan : Scope.Internet;
        }
        if (a.Length != 4) return Scope.Internet;
        if (a[0] == 127) return Scope.Loopback;
        return a[0] is 0 or 10
            || (a[0] == 172 && (a[1] & 0xF0) == 16)
            || (a[0] == 192 && a[1] == 168)
            || (a[0] == 169 && a[1] == 254)
            || (a[0] == 100 && (a[1] & 0xC0) == 64)                       // 100.64/10: carrier-grade NAT, which Tailscale uses
            || a[0] >= 224                                                // multicast and broadcast
            ? Scope.Lan : Scope.Internet;
    }
}
