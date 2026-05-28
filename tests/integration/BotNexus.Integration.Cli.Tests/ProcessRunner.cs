using System.Diagnostics;
using System.Text;

namespace BotNexus.Integration.Cli.Tests;

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public string Combined => StdOut + StdErr;
}

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        IDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        if (environment is not null)
        {
            foreach (var kvp in environment)
                psi.Environment[kvp.Key] = kvp.Value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        var outTask = ReadAllAsync(process.StandardOutput, stdOut, cancellationToken);
        var errTask = ReadAllAsync(process.StandardError, stdErr, cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } t)
            cts.CancelAfter(t);

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException(
                $"Process '{fileName} {arguments}' did not exit within {timeout}. " +
                $"Partial stdout: {stdOut}\nPartial stderr: {stdErr}");
        }

        await Task.WhenAll(outTask, errTask);
        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    private static async Task ReadAllAsync(StreamReader reader, StringBuilder sink, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
            sink.Append(buffer, 0, read);
    }

    /// <summary>
    /// Starts a process and returns when either the combined stdout/stderr matches <paramref name="matchPredicate"/>
    /// (the process is then killed) or the timeout expires. Useful for verifying that an interactive command
    /// reaches a specific prompt (e.g. an OAuth device-code line) without driving it to completion.
    /// </summary>
    public static async Task<WatchResult> RunUntilOutputMatchAsync(
        string fileName,
        string arguments,
        Func<string, bool> matchPredicate,
        TimeSpan timeout,
        IDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (environment is not null)
        {
            foreach (var kvp in environment)
                psi.Environment[kvp.Key] = kvp.Value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var combined = new StringBuilder();
        var matched = false;
        var matchedAt = TimeSpan.Zero;
        var started = DateTime.UtcNow;

        using var matchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        matchCts.CancelAfter(timeout);

        var lockObj = new object();
        void OnChunk(string chunk)
        {
            lock (lockObj)
            {
                combined.Append(chunk);
                if (!matched && matchPredicate(combined.ToString()))
                {
                    matched = true;
                    matchedAt = DateTime.UtcNow - started;
                    matchCts.Cancel();
                }
            }
        }

        var outTask = StreamChunksAsync(process.StandardOutput, OnChunk, cancellationToken);
        var errTask = StreamChunksAsync(process.StandardError, OnChunk, cancellationToken);

        try
        {
            await process.WaitForExitAsync(matchCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        try { await Task.WhenAll(outTask, errTask); }
        catch { /* readers fail once the process is killed; that's fine */ }

        return new WatchResult(matched, matchedAt, combined.ToString(), process.HasExited ? process.ExitCode : null);
    }

    private static async Task StreamChunksAsync(StreamReader reader, Action<string> onChunk, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        int read;
        while (!cancellationToken.IsCancellationRequested && (read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
            onChunk(new string(buffer, 0, read));
    }
}

internal sealed record WatchResult(bool Matched, TimeSpan MatchedAt, string Output, int? ExitCode);
