using System.Diagnostics;
using System.Text;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>
/// Wraps a <see cref="Process"/> with bounded output capture and lifecycle management.
/// Stdout and stderr are interleaved into a single circular buffer capped at <see cref="MaxOutputBytes"/>.
/// </summary>
public sealed class ManagedProcess : IDisposable
{
    internal const int MaxOutputBytes = 100 * 1024; // 100 KB

    private readonly Process _process;
    private readonly StringBuilder _outputBuffer = new();
    private readonly object _lock = new();
    private bool _disposed;

    internal ManagedProcess(Process process, string command, DateTimeOffset startedAt)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        Command = command;
        StartedAt = startedAt;
        Pid = process.Id;

        _process.OutputDataReceived += OnData;
        _process.ErrorDataReceived += OnData;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>
    /// Starts a process from <paramref name="startInfo"/> honouring <paramref name="cancellationToken"/>.
    /// Cancellation is re-checked <em>immediately</em> before <see cref="Process.Start()"/> so a token that
    /// was cancelled while arguments, workspace paths, environment variables or background slots were being
    /// validated cannot spawn a child at all. If cancellation is observed <em>after</em> the child started,
    /// the entire process tree is killed and the caller receives an <see cref="OperationCanceledException"/>;
    /// no <see cref="ManagedProcess"/> is produced, so callers never register a live orphan.
    /// </summary>
    internal static ManagedProcess Start(
        ProcessStartInfo startInfo,
        string command,
        CancellationToken cancellationToken = default)
        => Start(startInfo, command, cancellationToken, onStarted: null);

    /// <summary>
    /// Test seam for <see cref="Start(ProcessStartInfo, string, CancellationToken)"/>. <paramref name="onStarted"/>
    /// runs after the OS process exists but before the post-start cancellation check, letting tests
    /// deterministically exercise the "cancelled after start" branch and observe the resulting PID.
    /// </summary>
    internal static ManagedProcess Start(
        ProcessStartInfo startInfo,
        string command,
        CancellationToken cancellationToken,
        Action<Process>? onStarted)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        // Re-check immediately before Start(): everything between the caller's entry check and here
        // (argument construction, path/env validation, slot acquisition) can take arbitrary time.
        cancellationToken.ThrowIfCancellationRequested();

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start process.");
        }
        catch
        {
            process.Dispose();
            throw;
        }

        onStarted?.Invoke(process);

        if (cancellationToken.IsCancellationRequested)
        {
            // Lost the race: the child is live. Tear down the whole tree via the existing kill-tree path
            // and do NOT hand back a ManagedProcess, so nothing is registered and no slot stays consumed.
            KillTree(process);
            process.Dispose();
            throw new OperationCanceledException(cancellationToken);
        }

        return new ManagedProcess(process, command, DateTimeOffset.UtcNow);
    }

    public int Pid { get; }
    public string Command { get; }
    public DateTimeOffset StartedAt { get; }

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }
    }

    public int? ExitCode
    {
        get
        {
            try
            {
                return _process.HasExited ? _process.ExitCode : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Returns captured output. When <paramref name="tailLines"/> is specified,
    /// only the last N lines are returned.
    /// </summary>
    public string GetOutput(int? tailLines = null)
    {
        string snapshot;
        lock (_lock)
        {
            snapshot = _outputBuffer.ToString();
        }

        if (tailLines is null or <= 0)
            return snapshot;

        var lines = snapshot.Split('\n');
        var start = Math.Max(0, lines.Length - tailLines.Value);
        return string.Join('\n', lines.AsSpan(start));
    }

    /// <summary>Writes content to the process stdin if it is still running.</summary>
    public void WriteInput(string content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsRunning)
            throw new InvalidOperationException($"Process {Pid} has already exited.");

        _process.StandardInput.Write(content);
        _process.StandardInput.Flush();
    }

    /// <summary>
    /// Sends a graceful termination request, waits up to 5 seconds, then force-kills.
    /// </summary>
    public void Kill()
    {
        if (_disposed) return;

        try
        {
            if (_process.HasExited) return;

            _process.Kill(entireProcessTree: false);
            if (!_process.WaitForExit(5_000))
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2_000);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between our check and kill — safe to ignore.
        }
    }

    /// <summary>Waits for the process to exit, with an optional timeout in milliseconds.</summary>
    internal bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _process.OutputDataReceived -= OnData;
        _process.ErrorDataReceived -= OnData;

        KillTree(_process);
        _process.Dispose();
    }

    /// <summary>Best-effort force-kill of a process and its entire child tree.</summary>
    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        try { process.WaitForExit(2_000); } catch { /* best effort */ }
    }

    private void OnData(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        var clean = AnsiStripper.Strip(e.Data);
        lock (_lock)
        {
            _outputBuffer.AppendLine(clean);
            TrimBuffer();
        }
    }

    private void TrimBuffer()
    {
        // Approximate byte count via char length (sufficient for bounded buffer).
        if (_outputBuffer.Length * sizeof(char) <= MaxOutputBytes)
            return;

        var excess = _outputBuffer.Length - (MaxOutputBytes / sizeof(char));
        // Find the next newline after the excess to trim on a line boundary.
        var newlineIndex = -1;
        for (var i = excess; i < _outputBuffer.Length; i++)
        {
            if (_outputBuffer[i] == '\n')
            {
                newlineIndex = i;
                break;
            }
        }

        var removeCount = newlineIndex >= 0 ? newlineIndex + 1 : excess;
        _outputBuffer.Remove(0, (int)removeCount);
    }
}
