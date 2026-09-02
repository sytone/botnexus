using System.Diagnostics;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>
/// Wraps a <see cref="Process"/> with bounded output capture and lifecycle management.
/// Stdout and stderr are interleaved into a single circular buffer capped at <see cref="MaxOutputBytes"/>.
/// </summary>
public class ManagedProcess : IDisposable
{
    internal const int MaxOutputBytes = OutputRetentionPolicy.MaxOutputBytes;

    /// <summary>
    /// Message used when the OS declined to create the child (#2726). It states the retry-safe
    /// disposition explicitly, because "Failed to start process." left a caller unable to tell
    /// "nothing ran" from "something may have run" - and the latter must never be auto-retried.
    /// </summary>
    public const string NotDispatchedMessage =
        "Failed to start process - the command did not run and no side effect occurred. " +
        "It is safe to retry once the cause is resolved.";

    private readonly Process _process;
    private readonly BoundedOutputBuffer _outputBuffer = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Whether a termination request has been issued and, if so, whether the process was actually
    /// observed to exit. An <see cref="ProcessKillState.Unconfirmed"/> entry may still have live
    /// descendants, so callers must keep it tracked and re-killable rather than releasing its slot.
    /// </summary>
    public ProcessKillState KillState { get; private set; } = ProcessKillState.NotRequested;

    /// <summary>OS process name captured at start, so it stays readable after the process exits.</summary>
    public string ProcessName { get; }

    internal ManagedProcess(Process process, string command, DateTimeOffset startedAt)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        Command = command;
        StartedAt = startedAt;
        Pid = process.Id;
        ProcessName = SafeProcessName(process);

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
            // #2726: name the disposition. A start failure provably ran nothing, so the caller can
            // retry safely once the cause is resolved - the opposite of the unconfirmed-kill path
            // below, where the child was dispatched and its side effect may already have landed.
            if (!process.Start())
                throw new InvalidOperationException(NotDispatchedMessage);
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
        string banner;
        lock (_lock)
        {
            snapshot = _outputBuffer.RawSnapshot();
            banner = _outputBuffer.FormatBanner();
        }

        if (tailLines is > 0)
        {
            var lines = snapshot.Split('\n');
            var start = Math.Max(0, lines.Length - tailLines.Value);
            snapshot = string.Join('\n', lines.AsSpan(start));
        }

        // The banner leads the payload so the loss is visible before any output is read, and is
        // emitted ONLY when the cap actually discarded something - an untruncated buffer must stay
        // byte-identical to its pre-#3704 form.
        return banner.Length == 0 ? snapshot : $"{banner}\n{snapshot}";
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
    /// Sends a graceful termination request, waits up to 5 seconds, then force-kills the whole tree.
    /// Returns <c>true</c> only when exit was actually <em>observed</em>; <c>false</c> means the wait
    /// timed out and part of the tree may still be alive. Callers must not treat <c>false</c> as
    /// success - see <see cref="KillState"/>.
    /// </summary>
    public bool Kill()
    {
        if (_disposed) return false;

        var confirmed = KillCore();
        KillState = confirmed ? ProcessKillState.Confirmed : ProcessKillState.Unconfirmed;
        return confirmed;
    }

    /// <summary>
    /// Performs the actual termination and reports whether exit was observed. Virtual so tests can
    /// substitute a deterministic outcome for the inherently timing-dependent OS behaviour.
    /// </summary>
    protected virtual bool KillCore()
    {
        try
        {
            if (_process.HasExited) return true;

            _process.Kill(entireProcessTree: false);
            if (_process.WaitForExit(5_000))
                return true;

            _process.Kill(entireProcessTree: true);

            // The return value of this wait is the ONLY signal that the tree actually died.
            // Discarding it is what let orphaned descendants be reported as terminated.
            return _process.WaitForExit(2_000);
        }
        catch (InvalidOperationException)
        {
            // Process already exited between our check and kill - that IS a confirmed exit.
            return true;
        }
    }

    private static string SafeProcessName(Process process)
    {
        try { return process.ProcessName; }
        catch { return "unknown"; }
    }

    /// <summary>Waits for the process to exit, with an optional timeout in milliseconds.</summary>
    internal bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);

    public virtual void Dispose()
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
            // Cap enforcement, real UTF-8 byte accounting, grapheme-safe cutting and discarded-byte
            // tracking all live in BoundedOutputBuffer, which shares its disclosure wording with
            // ExecTool via OutputRetentionPolicy (#3704).
            _outputBuffer.AppendLine(clean);
        }
    }
}
