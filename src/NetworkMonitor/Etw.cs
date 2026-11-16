using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetworkMonitor;

/// <summary>
/// A real-time ETW session on Microsoft-Windows-Kernel-Network, with raw Windows calls
/// instead of TraceEvent. Structures are written by offset, for x64 only. The offsets
/// come from evntrace.h, evntcons.h and tdh.h. Starting a session needs admin rights
/// (see docs/etw-spike.md), so only the service and the test program use this.
/// </summary>
internal static class Etw
{
    internal static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;

    // EVENT_TRACE_PROPERTIES: 120 bytes on x64, then the session name.
    const int PropertiesSize = 120, NameBytes = 1024;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void EventRecordCallback(IntPtr eventRecord);

    static EventRecordCallback? _callback; // ETW calls it from native code, so it must not be collected
    static IntPtr _logfile, _loggerName;

    internal static void Start(string session)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("The ETW structures here are laid out for x64.");
        int err = StartWith(session);
        if (err == 183) // ERROR_ALREADY_EXISTS: left over from a run that crashed; sessions outlive their process
        {
            Stop(session);
            err = StartWith(session);
        }
        if (err != 0) throw new Win32Exception(err);
    }

    static int StartWith(string session)
    {
        IntPtr p = NewProperties();
        try
        {
            int err = StartTraceW(out ulong handle, session, p);
            if (err != 0) return err;
            var provider = KernelNetwork;
            err = EnableTraceEx2(handle, ref provider, 1 /* ENABLE_PROVIDER */, 4 /* information */,
                KeywordIpv4 | KeywordIpv6, 0, 0, IntPtr.Zero);
            if (err != 0) Stop(session);
            return err;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    static IntPtr NewProperties()
    {
        int total = PropertiesSize + NameBytes;
        IntPtr p = Marshal.AllocHGlobal(total);
        Marshal.Copy(new byte[total], 0, p, total);
        Marshal.WriteInt32(p, 0, total);          // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);             // Wnode.ClientContext: QPC timestamps
        Marshal.WriteInt32(p, 44, 0x00020000);    // Wnode.Flags: WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 64, 0x00000100);    // LogFileMode: EVENT_TRACE_REAL_TIME_MODE
        Marshal.WriteInt32(p, 68, 1);             // FlushTimer: 1 s, so events arrive promptly
        Marshal.WriteInt32(p, 116, PropertiesSize); // LoggerNameOffset
        return p;
    }

    /// <summary>Events and buffers the session dropped because the consumer fell behind.</summary>
    internal static (uint EventsLost, uint RealTimeBuffersLost) Query(string session)
    {
        IntPtr p = NewProperties();
        try
        {
            int err = ControlTraceW(0, session, p, 0 /* QUERY */);
            if (err != 0) throw new Win32Exception(err);
            return ((uint)Marshal.ReadInt32(p, 88), (uint)Marshal.ReadInt32(p, 100));
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    internal static void Stop(string session)
    {
        IntPtr p = NewProperties();
        try
        {
            ControlTraceW(0, session, p, 1 /* STOP */);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>Opens the session for reading, then calls <paramref name="onEvent"/> for every event on a thread of its own.</summary>
    /// <remarks>One consumer at a time: a second call replaces the first one's callback.</remarks>
    internal static (Thread Thread, ulong Handle) Consume(string session, EventRecordCallback onEvent)
    {
        _callback = onEvent;
        if (_loggerName != IntPtr.Zero) Marshal.FreeHGlobal(_loggerName);
        if (_logfile != IntPtr.Zero) Marshal.FreeHGlobal(_logfile);
        _loggerName = Marshal.StringToHGlobalUni(session);
        _logfile = Marshal.AllocHGlobal(448); // EVENT_TRACE_LOGFILEW on x64
        Marshal.Copy(new byte[448], 0, _logfile, 448);
        Marshal.WriteIntPtr(_logfile, 8, _loggerName);                  // LoggerName
        Marshal.WriteInt32(_logfile, 28, 0x00000100 | 0x10000000);      // REAL_TIME | EVENT_RECORD
        Marshal.WriteIntPtr(_logfile, 424, Marshal.GetFunctionPointerForDelegate(_callback)); // EventRecordCallback

        ulong handle = OpenTraceW(_logfile);
        if (handle == ulong.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error());
        var thread = new Thread(() => ProcessTrace([handle], 1, IntPtr.Zero, IntPtr.Zero))
        {
            IsBackground = true,
            Name = "ETW consumer",
        };
        thread.Start();
        return (thread, handle);
    }

    internal static void Close(ulong handle) => CloseTrace(handle);

    // EVENT_RECORD on x64.
    internal static ushort EventId(IntPtr record) => (ushort)Marshal.ReadInt16(record, 40);
    internal static int HeaderProcessId(IntPtr record) => Marshal.ReadInt32(record, 12);
    internal static int UserDataLength(IntPtr record) => (ushort)Marshal.ReadInt16(record, 86);
    internal static IntPtr UserData(IntPtr record) => Marshal.ReadIntPtr(record, 96);

    /// <summary>The event's fields as TDH decodes them from the provider's manifest: "PID:UInt32 size:UInt32 …".</summary>
    internal static string DescribeFields(IntPtr record)
    {
        uint size = 0;
        int err = TdhGetEventInformation(record, 0, IntPtr.Zero, IntPtr.Zero, ref size);
        if (err != 122) return $"(TDH error {err})"; // 122: ERROR_INSUFFICIENT_BUFFER, which is expected here
        IntPtr info = Marshal.AllocHGlobal((int)size);
        try
        {
            err = TdhGetEventInformation(record, 0, IntPtr.Zero, info, ref size);
            if (err != 0) return $"(TDH error {err})";
            int count = Marshal.ReadInt32(info, 104); // TRACE_EVENT_INFO.TopLevelPropertyCount
            var fields = new List<string>();
            for (int i = 0; i < count; i++)
            {
                IntPtr prop = info + 112 + i * 24;      // EVENT_PROPERTY_INFO[i]
                string name = Marshal.PtrToStringUni(info + Marshal.ReadInt32(prop, 4)) ?? "?";
                ushort inType = (ushort)Marshal.ReadInt16(prop, 8);
                ushort length = (ushort)Marshal.ReadInt16(prop, 18);
                fields.Add($"{name}:{InTypeName(inType)}{(inType == 14 ? $"[{length}]" : "")}");
            }
            return string.Join(" ", fields);
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    static string InTypeName(ushort t) => t switch
    {
        6 => "UInt16", 7 => "Int32", 8 => "UInt32", 9 => "Int64", 10 => "UInt64",
        14 => "Binary", 15 => "GUID", 16 => "Pointer", 20 => "HexInt32", 21 => "HexInt64",
        _ => $"InType{t}",
    };

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int StartTraceW(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int ControlTraceW(ulong handle, string name, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern ulong OpenTraceW(IntPtr logfile);

    [DllImport("advapi32.dll")]
    static extern int ProcessTrace(ulong[] handles, uint count, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    static extern int CloseTrace(ulong handle);

    [DllImport("tdh.dll")]
    static extern int TdhGetEventInformation(IntPtr record, uint contextCount, IntPtr context, IntPtr buffer, ref uint bufferSize);
}
