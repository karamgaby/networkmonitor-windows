using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetworkMonitor;

internal readonly record struct InterfaceSample(
    ulong Luid, string Alias, string Description, bool Counted, ulong InOctets, ulong OutOctets);

/// <summary>Byte counters of every network interface, from <c>GetIfTable2</c>.</summary>
internal static class InterfaceCounters
{
    const byte HardwareInterface = 0x01, FilterInterface = 0x02;
    const uint OperStatusUp = 1;

    // MIB_IF_TABLE2 is a ULONG count, then the rows, aligned to 8 (MIB_IF_ROW2 holds ULONG64s).
    internal const int RowsOffset = 8;

    /// <summary>
    /// Counted: physical NICs that are up, minus the NDIS filter instances that mirror them.
    /// That leaves out loopback, VPN tunnels (Tailscale, WireGuard, TAP) and Hyper-V/WSL vEthernet,
    /// whose traffic also crosses a physical NIC, so every byte is counted once.
    /// </summary>
    internal static bool IsCounted(byte flags, uint operStatus) =>
        (flags & HardwareInterface) != 0 && (flags & FilterInterface) == 0 && operStatus == OperStatusUp;

    internal static List<InterfaceSample> Read()
    {
        int err = GetIfTable2(out IntPtr table);
        if (err != 0) throw new Win32Exception(err);
        try
        {
            int count = Marshal.ReadInt32(table);
            int size = Marshal.SizeOf<MibIfRow2>();
            var rows = new List<InterfaceSample>(count);
            for (int i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<MibIfRow2>(table + RowsOffset + i * size);
                rows.Add(new(r.InterfaceLuid, r.Alias, r.Description,
                    IsCounted(r.InterfaceAndOperStatusFlags, r.OperStatus), r.InOctets, r.OutOctets));
            }
            return rows;
        }
        finally
        {
            FreeMibTable(table);
        }
    }

    [DllImport("iphlpapi.dll")]
    static extern int GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    static extern void FreeMibTable(IntPtr memory);
}

#pragma warning disable CS0649 // filled in by the marshaller
/// <summary>MIB_IF_ROW2 from netioapi.h: 1352 bytes, checked by a test.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MibIfRow2
{
    public ulong InterfaceLuid;
    public uint InterfaceIndex;
    public Guid InterfaceGuid;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
    public uint PhysicalAddressLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
    public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
    public byte InterfaceAndOperStatusFlags; // BOOLEAN bit fields: HardwareInterface, FilterInterface, ...
    public uint OperStatus, AdminStatus, MediaConnectState;
    public Guid NetworkGuid;
    public uint ConnectionType;
    public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
    public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos,
        InUcastOctets, InMulticastOctets, InBroadcastOctets;
    public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors,
        OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
}
#pragma warning restore CS0649
