using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using NetworkMonitor;

namespace EtwSpike;

/// <summary>
/// Answers three questions for step 2:
/// 1. Can it run without admin rights?
/// 2. What does it cost in CPU under load?
/// 3. Do the per-app totals match the network card's counters?
///
/// Usage: EtwSpike [--seconds N] [--log file] [--loopback-load]
/// With --loopback-load, a child process pushes TCP traffic over 127.0.0.1 as fast as it can.
/// That's a stress test with no internet use. It runs as a separate process, so its CPU
/// isn't counted here.
/// </summary>
static class Program
{
    enum Scope { Internet, Lan, Loopback }

    sealed class Totals
    {
        public long Received, Sent, LanBytes, LoopbackBytes, Events;
    }

    static readonly object Gate = new();
    static Dictionary<int, Totals> _byProcess = new();
    static readonly HashSet<ushort> Described = new();
    static Log _log = null!;

    static int Main(string[] args)
    {
        if (args is ["--loopback-child"]) return LoopbackChild();

        int seconds = 30;
        string? logPath = null;
        bool load = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds": seconds = int.Parse(args[++i]); break;
                case "--log": logPath = args[++i]; break;
                case "--loopback-load": load = true; break;
                default: Console.Error.WriteLine($"Unknown argument {args[i]}"); return 2;
            }
        }

        using var log = _log = new Log(logPath);
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        log.Line($"ETW spike, {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {seconds} s, admin={admin}, loopback load={load}");

        try
        {
            Etw.Start();
        }
        catch (Win32Exception e)
        {
            log.Line($"Can't start the session: error {e.NativeErrorCode}, {e.Message}");
            return 1;
        }
        log.Line("Session started.");

        bool cancelled = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancelled = true; };
        Process? loadChild = null;
        ulong handle = 0;
        try
        {
            (_, handle) = Etw.Consume(OnEvent);
            if (load) loadChild = StartLoopbackLoad();

            var meter = new RateMeter();
            meter.Update(CountedNics(), 1);
            var self = Process.GetCurrentProcess();
            TimeSpan cpuStart = self.TotalProcessorTime, cpuLast = cpuStart;
            var clock = Stopwatch.StartNew();
            double lastSeconds = 0;
            long eventsTotal = 0, nicTotal = 0, attributedTotal = 0;

            for (int elapsed = 0; elapsed < seconds && !cancelled; )
            {
                Thread.Sleep(5000);
                elapsed += 5;
                Dictionary<int, Totals> window;
                lock (Gate) (window, _byProcess) = (_byProcess, new());

                double now = clock.Elapsed.TotalSeconds, span = now - lastSeconds;
                lastSeconds = now;
                var (down, up) = meter.Update(CountedNics(), span);
                self.Refresh();
                TimeSpan cpu = self.TotalProcessorTime;
                double cpuPercent = 100 * (cpu - cpuLast).TotalSeconds / span;
                cpuLast = cpu;

                long events = window.Values.Sum(t => t.Events);
                long internetAndLan = window.Values.Sum(t => t.Received + t.Sent - t.LoopbackBytes);
                long loopback = window.Values.Sum(t => t.LoopbackBytes);
                long lan = window.Values.Sum(t => t.LanBytes);
                double nicBytes = (down + up) * span;
                eventsTotal += events;
                nicTotal += (long)nicBytes;
                attributedTotal += internetAndLan;

                log.Line("");
                log.Line($"[{elapsed,3} s] CPU {cpuPercent:0.00}% of one core   {events / span:n0} events/s");
                log.Line($"         card ↓↑ {SpeedFormat.Full(down)} / {SpeedFormat.Full(up)}   " +
                         $"ETW, not loopback {SpeedFormat.Full(internetAndLan / span)} (LAN {SpeedFormat.Full(lan / span)})   " +
                         $"loopback {SpeedFormat.Full(loopback / span)}   " +
                         $"ETW ÷ card {(nicBytes > 0 ? internetAndLan / nicBytes : 0):P0}");
                foreach (var (pid, t) in window.OrderByDescending(p => p.Value.Received + p.Value.Sent).Take(6))
                    log.Line($"         {Name(pid),-28} pid {pid,6}  ↓ {SpeedFormat.Full(t.Received / span),-11} ↑ {SpeedFormat.Full(t.Sent / span),-11} {t.Events / span,8:n0} ev/s");
            }

            var (lost, buffersLost) = Etw.Query();
            double total = clock.Elapsed.TotalSeconds;
            log.Line("");
            log.Line($"Overall: CPU {100 * (self.TotalProcessorTime - cpuStart).TotalSeconds / total:0.00}% of one core, " +
                     $"{eventsTotal / total:n0} events/s, events lost {lost}, real-time buffers lost {buffersLost}, " +
                     $"ETW ÷ card {(nicTotal > 0 ? (double)attributedTotal / nicTotal : 0):P1}");
        }
        finally
        {
            if (loadChild is { HasExited: false }) loadChild.Kill();
            if (handle != 0) Etw.Close(handle);
            Etw.Stop();
            log.Line("Session stopped.");
        }
        return 0;
    }

    static void OnEvent(IntPtr record)
    {
        ushort id = Etw.EventId(record);
        bool v4, send;
        switch (id)
        {
            case 10 or 42: (v4, send) = (true, true); break;   // TCPv4 / UDPv4 sent
            case 11 or 43: (v4, send) = (true, false); break;  // TCPv4 / UDPv4 received
            case 26 or 58: (v4, send) = (false, true); break;  // TCPv6 / UDPv6 sent
            case 27 or 59: (v4, send) = (false, false); break; // TCPv6 / UDPv6 received
            default: return;                                   // connects, retransmits, ...
        }

        IntPtr data = Etw.UserData(record);
        if (Etw.UserDataLength(record) < (v4 ? 16 : 40)) return; // PID, size, daddr, saddr
        int pid = Marshal.ReadInt32(data, 0);
        uint size = (uint)Marshal.ReadInt32(data, 4);
        int addrLen = v4 ? 4 : 16;
        var daddr = Address(data + 8, addrLen);
        var saddr = Address(data + 8 + addrLen, addrLen);
        Scope scope = Classify(daddr);

        if (Described.Add(id))
            _log.Line($"  event {id}: {Etw.DescribeFields(record)}\n" +
                      $"            sample: PID {pid} (header PID {Etw.HeaderProcessId(record)}), size {size}, daddr {daddr}, saddr {saddr} → {scope}");

        lock (Gate)
        {
            if (!_byProcess.TryGetValue(pid, out var t)) _byProcess[pid] = t = new Totals();
            if (send) t.Sent += size; else t.Received += size;
            if (scope == Scope.Lan) t.LanBytes += size;
            if (scope == Scope.Loopback) t.LoopbackBytes += size;
            t.Events++;
        }
    }

    static IPAddress Address(IntPtr p, int length)
    {
        var bytes = new byte[length];
        Marshal.Copy(p, bytes, 0, length);
        return new IPAddress(bytes);
    }

    // shortcut: the same rules step 2 will need, unit-tested there; here they only split the totals.
    static Scope Classify(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return Scope.Loopback;
        byte[] b = a.GetAddressBytes();
        if (b.Length == 4)
            return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && (b[1] & 0xC0) == 64) || b[0] >= 224
                ? Scope.Lan : Scope.Internet;
        return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal || a.IsIPv6Multicast || a.IsIPv6SiteLocal
            ? Scope.Lan : Scope.Internet;
    }

    static List<InterfaceSample> CountedNics() => InterfaceCounters.Read().Where(r => r.Counted).ToList();

    static readonly Dictionary<int, string> Names = new();

    static string Name(int pid)
    {
        if (Names.TryGetValue(pid, out var n)) return n;
        try
        {
            n = pid switch { 0 => "(pid 0: no process)", 4 => "System", _ => Process.GetProcessById(pid).ProcessName };
        }
        catch (ArgumentException)
        {
            n = "(exited)";
        }
        return Names[pid] = n;
    }

    static Process StartLoopbackLoad()
    {
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--loopback-child")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        _log.Line($"Loopback load: child pid {child.Id}");
        return child;
    }

    /// <summary>Both ends of one loopback TCP connection, writing 64 KB chunks until the parent kills it.</summary>
    static int LoopbackChild()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var receiver = Task.Run(() =>
        {
            using var s = listener.AcceptTcpClient().GetStream();
            var buffer = new byte[1 << 16];
            while (s.Read(buffer) > 0) { }
        });
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var stream = client.GetStream();
        var chunk = new byte[1 << 16];
        while (true) stream.Write(chunk); // killed by the parent
    }
}

sealed class Log : IDisposable
{
    readonly StreamWriter? _file;

    public Log(string? path)
    {
        if (path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _file = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public void Line(string text)
    {
        lock (this)
        {
            Console.WriteLine(text);
            _file?.WriteLine(text);
        }
    }

    public void Dispose() => _file?.Dispose();
}
