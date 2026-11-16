using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NetworkMonitor;

/// <summary>
/// The service's work: Kernel-Network ETW events → per-app totals in a <see cref="UsageBook"/>,
/// folded in every 10 s, saved every minute, served over <see cref="UsagePipe"/>.
/// </summary>
internal sealed class UsageCollector : IDisposable
{
    const string Session = "NetworkMonitor";
    const int TickMs = 10_000, SaveEveryTicks = 6;

    readonly UsageBook _book;
    readonly RateMeter _card = new();
    readonly System.Threading.Timer _timer;
    readonly CancellationTokenSource _stop = new();
    readonly Task _pipe;
    readonly object _traceGate = new();
    Thread? _consumer;
    ulong _trace;
    int _ticks;

    // Names are looked up on the consumer thread only. A PID's name is looked up again in each
    // 10 s window, so a reused PID is put right within one window. _lastPaths keeps the path of a
    // process that has since exited, for its late receive events.
    volatile int _window;
    int _seenWindow = -1;
    readonly Dictionary<int, string> _windowNames = new(), _lastPaths = new();

    internal UsageCollector()
    {
        _book = new UsageBook(Path.Combine(ServiceLog.DataFolder, "usage"), Today);
        CardDelta(); // baseline
        StartTrace();
        _timer = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
        _pipe = Task.Run(ServePipe);
    }

    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    void StartTrace()
    {
        lock (_traceGate)
        {
            if (_trace != 0) Etw.Close(_trace);
            Etw.Start(Session);
            (_consumer, _trace) = Etw.Consume(Session, OnEvent);
        }
    }

    void Tick()
    {
        try
        {
            if (_consumer is { IsAlive: false } && !_stop.IsCancellationRequested)
            {
                ServiceLog.Write("The ETW session ended (stopped from outside?); starting it again.");
                StartTrace();
            }
            var (down, up) = CardDelta();
            _window++;
            _book.Flush(Today, down, up);
            if (++_ticks % SaveEveryTicks == 0) _book.Save();
        }
        catch (Exception e)
        {
            ServiceLog.Write($"Tick failed: {e.Message}");
        }
    }

    (long Down, long Up) CardDelta()
    {
        try
        {
            var (down, up) = _card.Update(InterfaceCounters.Read().Where(r => r.Counted), 1); // per 1 s = the delta
            return ((long)down, (long)up);
        }
        catch (Win32Exception)
        {
            return (0, 0); // adapters in flux; the meter keeps its baseline, so the next tick covers both
        }
    }

    async Task ServePipe()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await UsagePipe.Serve(UsagePipe.Name, r => _book.Read(r.From, r.To), _stop.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                ServiceLog.Write($"Pipe failed, retrying in 5 s: {e.Message}");
                try { await Task.Delay(5000, _stop.Token); } catch (OperationCanceledException) { }
            }
        }
    }

    unsafe void OnEvent(IntPtr record)
    {
        bool v4, send;
        switch (Etw.EventId(record))
        {
            case 10 or 42: (v4, send) = (true, true); break;   // TCPv4 / UDPv4 sent
            case 11 or 43: (v4, send) = (true, false); break;  // TCPv4 / UDPv4 received
            case 26 or 58: (v4, send) = (false, true); break;  // TCPv6 / UDPv6 sent
            case 27 or 59: (v4, send) = (false, false); break; // TCPv6 / UDPv6 received
            default: return;                                   // connects, retransmits, ...
        }
        if (Etw.UserDataLength(record) < (v4 ? 16 : 40)) return; // PID, size, daddr, saddr
        byte* data = (byte*)Etw.UserData(record);
        int pid = *(int*)data;               // the payload's PID; the header's is whoever was running
        uint size = *(uint*)(data + 4);
        var scope = AddressScope.Classify(new ReadOnlySpan<byte>(data + 8, v4 ? 4 : 16)); // daddr: the remote end
        if (scope == Scope.Loopback) return;
        _book.Add(AppFor(pid), scope == Scope.Lan, send, size);
    }

    string AppFor(int pid)
    {
        int window = _window;
        if (window != _seenWindow)
        {
            _seenWindow = window;
            _windowNames.Clear();
            if (_lastPaths.Count > 4096) _lastPaths.Clear();
        }
        if (_windowNames.TryGetValue(pid, out var app)) return app;
        app = pid is 0 or 4 ? DayUsage.SystemApp
            : ProcessPath(pid) is { } path ? _lastPaths[pid] = path
            : _lastPaths.GetValueOrDefault(pid, DayUsage.Exited);
        return _windowNames[pid] = app;
    }

    static unsafe string? ProcessPath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            char* buffer = stackalloc char[1024];
            int size = 1024;
            return QueryFullProcessImageNameW(h, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        using (var done = new ManualResetEvent(false))
        {
            if (_timer.Dispose(done)) done.WaitOne(); // let a running tick finish
        }
        lock (_traceGate)
        {
            if (_trace != 0) Etw.Close(_trace);
            Etw.Stop(Session);
            _trace = 0;
        }
        _consumer?.Join(2000);
        var (down, up) = CardDelta(); // the last few seconds
        _book.Flush(Today, down, up);
        _book.Save();
        _pipe.Wait(2000);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern unsafe bool QueryFullProcessImageNameW(IntPtr process, int flags, char* name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
