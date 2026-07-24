using System.Diagnostics;
using System.Text;

namespace BotNexus.Integration.ExtensionBoot.Tests;

/// <summary>
/// Cross-platform helper for invoking subprocesses with output capture and timeouts.
/// Self-contained so this seam project needs no ProjectReference to another test project.
/// </summary>
public static class ProcessRunner
{
    public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
    {
        public string Combined => string.IsNullOrEmpty(StdErr) ? StdOut : $"{StdOut}{Environment.NewLine}{StdErr}";
    }

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        IDictionary<string, string?>? environment = null,
        string? workingDirectory = null,
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
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
            {
                if (v is null)
                    psi.Environment.Remove(k);
                else
                    psi.Environment[k] = v;
            }
        }

        using var process = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdOut) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stdErr) stdErr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{fileName}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is not null)
            combinedCts.CancelAfter(timeout.Value);

        try
        {
            await process.WaitForExitAsync(combinedCts.Token);
        }
        catch (OperationCanceledException) when (combinedCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException(
                $"Process '{fileName} {arguments}' did not exit within {timeout}.\nStdOut:\n{stdOut}\nStdErr:\n{stdErr}");
        }

        return new ProcessResult(process.ExitCode, stdOut.ToString(), stdErr.ToString());
    }

    /// <summary>
    /// Launch a long-running process and return immediately. Caller owns the lifetime
    /// and must dispose the returned wrapper to terminate the child cleanly.
    /// </summary>
    public static BackgroundProcess StartBackground(
        string fileName,
        string arguments,
        IDictionary<string, string?>? environment = null,
        string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
            {
                if (v is null)
                    psi.Environment.Remove(k);
                else
                    psi.Environment[k] = v;
            }
        }

        var process = new Process { StartInfo = psi };
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdOut) stdOut.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stdErr) stdErr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new BackgroundProcess(process, stdOut, stdErr);
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    public sealed class BackgroundProcess : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly StringBuilder _stdOut;
        private readonly StringBuilder _stdErr;
        private bool _disposed;

        public BackgroundProcess(Process process, StringBuilder stdOut, StringBuilder stdErr)
        {
            _process = process;
            _stdOut = stdOut;
            _stdErr = stdErr;
        }

        public int ProcessId => _process.Id;
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

        public string SnapshotStdOut() { lock (_stdOut) return _stdOut.ToString(); }
        public string SnapshotStdErr() { lock (_stdErr) return _stdErr.ToString(); }
        public string SnapshotCombined() => $"{SnapshotStdOut()}{Environment.NewLine}{SnapshotStdErr()}";

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await _process.WaitForExitAsync(killCts.Token); } catch { /* swallow */ }
                }
            }
            catch { /* best-effort */ }
            finally { _process.Dispose(); }
        }
    }
}
