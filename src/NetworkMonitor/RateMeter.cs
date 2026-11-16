namespace NetworkMonitor;

/// <summary>Turns successive byte counters into bytes per second, per interface.</summary>
internal sealed class RateMeter
{
    Dictionary<ulong, (ulong In, ulong Out)> _previous = new();

    /// <summary>
    /// Only interfaces seen in both samples count: one that just came up has no baseline yet,
    /// and one that went away (or whose counter went backwards on a driver reset) adds nothing,
    /// so the rate never jumps or goes negative when adapters change.
    /// </summary>
    public (double Down, double Up) Update(IEnumerable<InterfaceSample> samples, double seconds)
    {
        ulong received = 0, sent = 0;
        var next = new Dictionary<ulong, (ulong In, ulong Out)>();
        foreach (var s in samples)
        {
            next[s.Luid] = (s.InOctets, s.OutOctets);
            if (!_previous.TryGetValue(s.Luid, out var p)) continue;
            if (s.InOctets >= p.In) received += s.InOctets - p.In;
            if (s.OutOctets >= p.Out) sent += s.OutOctets - p.Out;
        }
        _previous = next;
        return seconds > 0 ? (received / seconds, sent / seconds) : (0, 0);
    }
}
