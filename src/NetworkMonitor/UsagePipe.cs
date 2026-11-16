using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace NetworkMonitor;

/// <summary>
/// How the tray app asks the service for totals. One JSON line each way:
/// <c>{"From":"2026-11-01","To":"2026-11-16"}</c> → a <see cref="DayUsage"/>, or <c>{"Error":"…"}</c>.
/// </summary>
internal static class UsagePipe
{
    internal const string Name = "NetworkMonitor";
    internal const int MaxDays = 400;
    const int MaxRequestBytes = 1024, MaxReplyBytes = 16 << 20;

    internal sealed record Request(DateOnly From, DateOnly To);

    /// <summary>Null for anything that isn't a well-formed request for at most <see cref="MaxDays"/> days.</summary>
    internal static Request? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            var r = JsonSerializer.Deserialize<Request>(line);
            return r is not null && r.From <= r.To && r.To.DayNumber - r.From.DayNumber < MaxDays ? r : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Answers requests one at a time until <paramref name="stop"/>. Only SYSTEM and people signed
    /// in at this PC (the INTERACTIVE group) can connect; network logons can't. The pipe is created
    /// once with FirstPipeInstance and reused, so nothing else can take the name while the service runs.
    /// </summary>
    internal static async Task Serve(string name, Func<Request, DayUsage> answer, CancellationToken stop)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow)); // ReadWrite leaves out CreateNewInstance
        using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);

        while (!stop.IsCancellationRequested)
        {
            await pipe.WaitForConnectionAsync(stop);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(2000); // a client that connects and goes quiet can't hold the pipe
            try
            {
                byte[]? line = await ReadLine(pipe, MaxRequestBytes, timeout.Token);
                Request? request = line is null ? null : Parse(line);
                byte[] reply = request is null
                    ? JsonSerializer.SerializeToUtf8Bytes(new { Error = "bad request" })
                    : JsonSerializer.SerializeToUtf8Bytes(answer(request));
                await pipe.WriteAsync(reply, timeout.Token);
                await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
                // Disconnecting throws away unread bytes, so wait for the client to close its end.
                await pipe.ReadAsync(new byte[1], timeout.Token);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
            catch (IOException) { } // the client went away
            finally
            {
                try { pipe.Disconnect(); } catch (InvalidOperationException) { }
            }
        }
    }

    /// <summary>Null when the service isn't running or doesn't answer.</summary>
    internal static async Task<DayUsage?> Ask(string name, Request request, int timeoutMs = 2000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(request), timeout.Token);
            await pipe.WriteAsync("\n"u8.ToArray(), timeout.Token);
            byte[]? line = await ReadLine(pipe, MaxReplyBytes, timeout.Token);
            return line is null ? null : JsonSerializer.Deserialize<DayUsage>(line);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException
                                     or TimeoutException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Bytes up to the first '\n', or null if the stream ends first or the line is longer than <paramref name="max"/>.</summary>
    static async Task<byte[]?> ReadLine(Stream s, int max, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            int n = await s.ReadAsync(chunk, ct);
            if (n == 0) return null;
            int end = Array.IndexOf(chunk, (byte)'\n', 0, n);
            buffer.Write(chunk, 0, end < 0 ? n : end);
            if (buffer.Length > max) return null;
            if (end >= 0) return buffer.ToArray();
        }
    }
}
