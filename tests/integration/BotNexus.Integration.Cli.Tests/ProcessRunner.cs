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
}
