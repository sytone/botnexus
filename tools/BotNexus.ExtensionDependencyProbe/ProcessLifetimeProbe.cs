using System.Diagnostics;
using System.IO.Pipes;

/// <summary>Framework-only synthetic child modes; readiness precedes observation, no product runtime.</summary>
internal static class ProcessLifetimeProbe
{
    internal static async Task RunAsync(string[] args)
    {
        switch (args[1])
        {
            case "normal":
                Console.WriteLine("NORMAL_STDOUT");
                Console.Error.WriteLine("NORMAL_STDERR");
                return;
            case "timeout":
                Console.WriteLine("TIMEOUT_READY");
                using (var ready = new NamedPipeClientStream(".", args[2], PipeDirection.InOut, PipeOptions.Asynchronous))
                using (var lifecycle = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    await ready.ConnectAsync(lifecycle.Token);
                    await WriteIdentityAsync(ready, args[3], lifecycle.Token);
                    var ack = new byte[1];
                    if (await ready.ReadAsync(ack, lifecycle.Token) != 1) throw new IOException("Readiness acknowledgement missing.");
                    await Task.Delay(Timeout.InfiniteTimeSpan, lifecycle.Token);
                }
                return;
            case "parent":
                using (var child = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                {
                    UseShellExecute = false,
                    ArgumentList = { typeof(ProcessLifetimeProbe).Assembly.Location, "process-lifetime", "hold-pipe", args[2], args[3] },
                }) ?? throw new InvalidOperationException("Cannot start synthetic pipe holder."))
                using (var ready = new NamedPipeClientStream(".", args[2] + "-parent", PipeDirection.InOut, PipeOptions.Asynchronous))
                using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    try
                    {
                        Console.WriteLine("PIPE_CHILD " + child.Id);
                        await ready.ConnectAsync(deadline.Token);
                        var identity = System.Text.Encoding.UTF8.GetBytes($"{child.Id}:{args[3]}\n");
                        await ready.WriteAsync(identity, deadline.Token);
                        await ready.FlushAsync(deadline.Token);
                        var ack = new byte[1];
                        if (await ready.ReadAsync(ack, deadline.Token) != 1) throw new IOException("Child ownership acknowledgement missing.");
                    }
                    catch (Exception primary)
                    {
                        // Parent retains ownership until the test acknowledges its verified handle.
                        try
                        {
                            if (!child.HasExited) child.Kill(entireProcessTree: true);
                            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            await child.WaitForExitAsync(cleanup.Token);
                        }
                        catch (Exception cleanupError) { throw new AggregateException("Parent transfer and child cleanup failed.", primary, cleanupError); }
                        throw;
                    }
                }
                return;
            case "hold-pipe":
                await HoldPipeAsync(args[2], args[3]);
                return;
            default:
                throw new ArgumentException("Unknown process lifetime mode.");
        }
    }

    private static async Task HoldPipeAsync(string pipeName, string nonce)
    {
        using var lifecycle = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(lifecycle.Token);
        await WriteIdentityAsync(pipe, nonce, lifecycle.Token);
        var release = ReadReleaseAsync(pipe, lifecycle.Token);

        Exception? primary = null;
        try
        {
            await release; // EOF/invalid signal fails; lifecycle bounds loss of the test owner.
        }
        catch (Exception error) { primary = error; }
        finally
        {
            lifecycle.Cancel();
            try
            {
                await ObserveCancellationAsync(release);
            }
            catch (Exception cleanupError)
            {
                if (primary is not null) throw new AggregateException("Holder primary and cleanup failures.", primary, cleanupError);
                throw;
            }
        }
        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static async Task WriteIdentityAsync(Stream stream, string nonce, CancellationToken token)
    {
        using var self = Process.GetCurrentProcess();
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{self.Id}:{nonce}\n");
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    private static async Task ReadReleaseAsync(Stream pipe, CancellationToken token)
    {
        var signal = new byte[1];
        if (await pipe.ReadAsync(signal, token) != 1 || signal[0] != 1)
            throw new IOException("Explicit child release missing or invalid.");
    }

    private static async Task ObserveCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }
}
