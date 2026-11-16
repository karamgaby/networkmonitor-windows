using System.Runtime.InteropServices;

namespace NetworkMonitor;

/// <summary>
/// Runs as a Windows service through the SCM's own calls. .NET 8 ships no ServiceBase, and the
/// package that has it isn't worth a download for this much code.
/// </summary>
internal static class WindowsService
{
    internal const string Name = "NetworkMonitor";

    const int OwnProcess = 0x10;
    const int Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    const int ControlStop = 1, ControlInterrogate = 4, ControlShutdown = 5;
    const int AcceptStop = 1, AcceptShutdown = 4;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void ServiceMainFn(int argc, IntPtr argv);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int HandlerFn(int control, int eventType, IntPtr eventData, IntPtr context);

    // The SCM calls these from native code, so they must not be collected.
    static ServiceMainFn? _main;
    static HandlerFn? _handler;
    static IntPtr _statusHandle;
    static readonly ManualResetEventSlim StopRequested = new();

    /// <summary>
    /// Blocks until the service stops. <paramref name="start"/> does the work and returns what to
    /// dispose on stop. Returns 1063 when it wasn't started by the SCM (run from a terminal).
    /// </summary>
    internal static int Run(Func<IDisposable> start)
    {
        _main = (_, _) => ServiceMain(start);
        var table = new[]
        {
            new ServiceTableEntry { Name = Name, Proc = Marshal.GetFunctionPointerForDelegate(_main) },
            default,
        };
        return StartServiceCtrlDispatcherW(table) ? 0 : Marshal.GetLastWin32Error();
    }

    static void ServiceMain(Func<IDisposable> start)
    {
        _handler = Handler;
        _statusHandle = RegisterServiceCtrlHandlerExW(Name, _handler, IntPtr.Zero);
        Report(StartPending, waitHint: 10_000);
        IDisposable work;
        try
        {
            work = start();
        }
        catch (Exception e)
        {
            ServiceLog.Write($"Couldn't start: {e}");
            Report(Stopped, exitCode: 1064); // ERROR_EXCEPTION_IN_SERVICE
            return;
        }
        ServiceLog.Write("Started.");
        Report(Running);
        StopRequested.Wait();
        Report(StopPending, waitHint: 10_000);
        try
        {
            work.Dispose();
        }
        catch (Exception e)
        {
            ServiceLog.Write($"Error while stopping: {e}");
        }
        ServiceLog.Write("Stopped.");
        Report(Stopped);
    }

    static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case ControlStop or ControlShutdown: StopRequested.Set(); return 0;
            case ControlInterrogate: return 0;
            default: return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }
    }

    static int _checkPoint;

    static void Report(int state, int exitCode = 0, int waitHint = 0)
    {
        var status = new ServiceStatus
        {
            Type = OwnProcess,
            State = state,
            ControlsAccepted = state == Running ? AcceptStop | AcceptShutdown : 0,
            Win32ExitCode = exitCode,
            CheckPoint = state is StartPending or StopPending ? ++_checkPoint : 0,
            WaitHint = waitHint,
        };
        SetServiceStatus(_statusHandle, ref status);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ServiceTableEntry
    {
        public string? Name;
        public IntPtr Proc;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus
    {
        public int Type, State, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool StartServiceCtrlDispatcherW(ServiceTableEntry[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerFn handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}

/// <summary>%ProgramData%\NetworkMonitor\service.log, kept under 1 MB (one older file is kept as .old).</summary>
internal static class ServiceLog
{
    static readonly object Gate = new();

    internal static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetworkMonitor");

    internal static void Write(string text)
    {
        string path = Path.Combine(DataFolder, "service.log");
        try
        {
            lock (Gate)
            {
                if (new FileInfo(path) is { Exists: true, Length: > 1_000_000 }) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}\r\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the counting.
        }
    }
}
