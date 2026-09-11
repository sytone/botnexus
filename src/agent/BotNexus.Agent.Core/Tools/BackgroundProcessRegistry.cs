using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BotNexus.Agent.Core.Tools;

/// <summary>
/// Owns launched children in the host-shared assembly so separately loaded extensions see the same
/// handles. Owner keys are supplied by trusted tool contributors, never by tool-call arguments.
/// No operation attaches to an arbitrary OS PID. Running and unconfirmed-kill entries are never reaped.
/// </summary>
public sealed class BackgroundProcessRegistry
{
    /// <summary>The host-wide lifecycle owner, shared across extension load contexts.</summary>
    public static BackgroundProcessRegistry Instance { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, (string Owner, BackgroundProcess Process)> _processes = [];
    private readonly int _maxExitedRetained;

    /// <summary>Allows isolated hosts/tests to choose a completed-entry retention cap.</summary>
    public BackgroundProcessRegistry(int maxExitedRetained = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxExitedRetained);
        _maxExitedRetained = maxExitedRetained;
    }

    /// <summary>Transfers a live wrapper to the registry without replacing a still-owned PID.</summary>
    public void Register(string owner, BackgroundProcess process)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(process);
        lock (_gate)
        {
            if (_processes.TryGetValue(process.Pid, out var previous))
            {
                if (ReferenceEquals(previous.Process, process)) return;
                if (previous.Process.IsRunning || previous.Process.KillUnconfirmed)
                    throw new InvalidOperationException("PID is still owned by a tracked process.");
                previous.Process.Dispose();
            }
            _processes[process.Pid] = (owner, process);
            ReapCore();
        }
    }

    /// <summary>Returns only children owned by the calling agent; absence never proves an OS process died.</summary>
    public BackgroundProcess? Get(string owner, int pid)
    {
        lock (_gate)
            return _processes.TryGetValue(pid, out var entry) && entry.Owner == owner ? entry.Process : null;
    }

    /// <summary>Reaps completed entries, then snapshots only this owner's registrations.</summary>
    public IReadOnlyList<BackgroundProcess> List(string owner)
    {
        lock (_gate)
        {
            ReapCore();
            return _processes.Values.Where(p => p.Owner == owner).Select(p => p.Process).ToArray();
        }
    }

    /// <summary>Bounds completed registrations without evicting running or uncertain children.</summary>
    public void Reap()
    {
        lock (_gate) ReapCore();
    }

    private void ReapCore()
    {
        var exited = _processes.Values.Where(p => p.Process.IsComplete && !p.Process.KillUnconfirmed)
            .OrderBy(p => p.Process.StartedAt).ToArray();
        foreach (var entry in exited.Take(Math.Max(0, exited.Length - _maxExitedRetained)))
        {
            if (entry.Process.TryReleaseCompleted())
                _processes.Remove(entry.Process.Pid);
        }
    }

    /// <summary>Releases an explicitly owned entry; callers must arrange safe lifecycle cleanup first.</summary>
    public bool Remove(string owner, int pid)
    {
        lock (_gate)
        {
            if (!_processes.TryGetValue(pid, out var entry) || entry.Owner != owner) return false;
            if (entry.Process.IsRunning || entry.Process.KillUnconfirmed) return false;
            return _processes.Remove(pid);
        }
    }

    /// <summary>Disposes this owner's entries during isolated-host cleanup, never another owner's children.</summary>
    public void Clear(string owner)
    {
        lock (_gate)
        {
            foreach (var entry in _processes.Values.Where(p => p.Owner == owner).ToArray())
            {
                entry.Process.Dispose();
                if (!entry.Process.IsRunning && !entry.Process.KillUnconfirmed)
                    _processes.Remove(entry.Process.Pid);
            }
        }
    }
}

/// <summary>
/// Owns a started process, bounded stream drains, and stdin until confirmed completion. Completion
/// includes both stream drains; seeing OS exit alone must not truncate the child's final output.
/// Background lifetimes are independent of the launching call after ownership transfer.
/// </summary>
public class BackgroundProcess : IDisposable
{
    private readonly Process _process;
    private readonly object _gate = new();
    private readonly BackgroundOutputBuffer _output = new();
    private readonly Task _completion;
    private readonly object _lifecycle = new();
    private volatile bool _disposed;
    private int? _exitCode;

    /// <summary>Adopts a process started by a trusted caller; output must be redirected and unread.</summary>
    public BackgroundProcess(Process process, string command, DateTimeOffset startedAt)
    {
        _process = process ?? throw new ArgumentNullException(nameof(process));
        Pid = process.Id;
        Command = command;
        StartedAt = startedAt;
        try { ProcessName = process.ProcessName; }
        catch (InvalidOperationException) { ProcessName = "exited"; }
        _completion = CompleteAsync(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError));
    }

    /// <summary>Captured PID remains available after handle disposal.</summary>
    public int Pid { get; }
    /// <summary>Original command retained for status and diagnostics.</summary>
    public string Command { get; }
    /// <summary>Launch time used for deterministic oldest-first retention.</summary>
    public DateTimeOffset StartedAt { get; }
    /// <summary>Captured OS name for diagnostics after termination.</summary>
    public string ProcessName { get; }
    /// <summary>True until both process exit and redirected output drains finish.</summary>
    public bool IsComplete => _completion.IsCompletedSuccessfully;
    /// <summary>An unconfirmed kill pins the registration even if the root has exited.</summary>
    public bool KillUnconfirmed { get; private set; }
    /// <summary>Distinguishes normal completion from a requested termination.</summary>
    public bool KillRequested { get; private set; }
    /// <summary>OS liveness, independent of pending final output.</summary>
    public bool IsRunning
    {
        get { try { return !_process.HasExited; } catch (InvalidOperationException) { return false; } }
    }
    /// <summary>Cached exit code survives retention and handle disposal.</summary>
    public int? ExitCode
    {
        get { try { return _process.HasExited ? _process.ExitCode : null; } catch (InvalidOperationException) { return _exitCode; } }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        // Read fixed-size chunks, not ReadLine: one unbroken line must not bypass the memory cap.
        var buffer = new char[4096];
        var decoder = new BackgroundOutputDecoder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer).ConfigureAwait(false);
            var clean = decoder.Append(buffer.AsSpan(0, count), final: count == 0);
            lock (_gate) _output.AppendChunk(clean);
            if (count == 0) return;
        }
    }

    private async Task CompleteAsync(Task stdout, Task stderr)
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            _exitCode = _process.ExitCode;
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Disposal may close redirected pipes while a drain is pending. Observe both tasks.
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (Exception drainError) when (drainError is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }

    /// <summary>Waits for exit AND final output; cancellation cancels the wait, not the owned child.</summary>
    public Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
        => _completion.WaitAsync(cancellationToken);

    /// <summary>Compatibility wait whose successful return guarantees final output is available.</summary>
    public bool WaitForExit(int milliseconds) => _completion.Wait(milliseconds);

    /// <summary>Returns bounded tail output, disclosing dropped bytes even when requesting only a few lines.</summary>
    public string GetOutput(int? tailLines = null)
    {
        lock (_gate)
        {
            var text = _output.RawSnapshot();
            if (tailLines is > 0)
            {
                var lines = text.Split('\n');
                var count = lines.Length - (text.EndsWith('\n') ? 1 : 0);
                var start = Math.Max(0, count - tailLines.Value);
                text = string.Join('\n', lines.AsSpan(start, count - start));
            }
            var banner = _output.FormatBanner();
            return banner.Length == 0 ? text : $"{banner}\n{text}";
        }
    }

    /// <summary>Writes interactive input only to the retained child handle, never a PID reattachment.</summary>
    public void WriteInput(string content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsRunning) throw new InvalidOperationException($"Process {Pid} has already exited.");
        _process.StandardInput.Write(content);
        _process.StandardInput.Flush();
    }

    /// <summary>Supplied launch input is finite: write it while output drains, then signal EOF.</summary>
    public async Task WriteInitialInputAsync(string input, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
        _process.StandardInput.Close();
    }

    /// <summary>Requests a tree kill and preserves uncertain registrations for a later retry.</summary>
    public bool Kill()
    {
        lock (_lifecycle)
        {
            if (_disposed) return false;
            KillRequested = true;
            var confirmed = KillCore();
            KillUnconfirmed = !confirmed;
            // Preserve the synchronous kill/reap contract: a confirmed root exit must also give
            // the bounded drains an opportunity to publish their final output before reaping.
            if (confirmed) _completion.Wait(2_000);
            return confirmed;
        }
    }

    /// <summary>Kill the tree BEFORE its root disappears; a failed observation must remain explicit.</summary>
    protected virtual bool KillCore()
    {
        try
        {
            if (_process.HasExited) return !KillUnconfirmed;
            _process.Kill(entireProcessTree: true);
            return _process.WaitForExit(2_000);
        }
        catch (InvalidOperationException) { return !KillUnconfirmed; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>Releases stream and OS resources only when the owner explicitly ends this lifetime.</summary>
    public virtual void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            if (IsRunning && !Kill()) return;
            if (KillUnconfirmed) return;
            _disposed = true;
            _process.Dispose();
        }
    }

    // Reaping and kill publication share a lock: an exited root cannot be evicted while the
    // termination operation is still deciding whether descendants may remain alive.
    internal bool TryReleaseCompleted()
    {
        lock (_lifecycle)
        {
            if (!IsComplete || KillUnconfirmed) return false;
            Dispose();
            return _disposed;
        }
    }
}

/// <summary>Shared UTF-8-byte-bounded tail buffer; extensions must not maintain divergent caps.</summary>
public class BackgroundOutputBuffer
{
    private readonly StringBuilder _buffer = new();
    private readonly int _maxOutputBytes;
    /// <summary>Allows small caps in deterministic retention tests.</summary>
    public BackgroundOutputBuffer(int maxOutputBytes = OutputRetentionPolicy.MaxOutputBytes) => _maxOutputBytes = maxOutputBytes;
    /// <summary>Actual retained UTF-8 bytes, excluding disclosure.</summary>
    public long RetainedBytes { get; private set; }
    /// <summary>Cumulative UTF-8 bytes discarded from the head.</summary>
    public long DiscardedBytes { get; private set; }
    /// <summary>Line-oriented compatibility entry point.</summary>
    public void AppendLine(string value) => AppendChunk(value + Environment.NewLine);
    /// <summary>Appends bounded reader chunks without requiring a newline.</summary>
    public void AppendChunk(string value)
    {
        _buffer.Append(value);
        var text = _buffer.ToString();
        // Count the concatenated bounded payload so the encoding owns split-scalar accounting.
        // Production appends at most 4096 characters; no independent surrogate policy is needed.
        RetainedBytes = Encoding.UTF8.GetByteCount(text);
        if (RetainedBytes <= _maxOutputBytes) return;
        var cut = 0;
        long shed = 0;
        while (cut < text.Length && RetainedBytes - shed > _maxOutputBytes)
        {
            var width = StringInfo.GetNextTextElementLength(text.AsSpan(cut));
            shed += Encoding.UTF8.GetByteCount(text.AsSpan(cut, width));
            cut += width;
        }
        var newline = text.IndexOf('\n', cut);
        if (newline >= cut && newline - cut <= 8192)
        {
            shed += Encoding.UTF8.GetByteCount(text.AsSpan(cut, newline + 1 - cut));
            cut = newline + 1;
        }
        _buffer.Remove(0, cut);
        RetainedBytes -= shed;
        DiscardedBytes += shed;
    }
    /// <summary>Returns the bounded payload without disclosure for byte-accounting callers.</summary>
    public string RawSnapshot() => _buffer.ToString();
    /// <summary>Uses the common head/tail loss contract rather than a local truncation message.</summary>
    public string FormatBanner() => DiscardedBytes == 0 ? string.Empty : OutputRetentionPolicy.FormatTruncationBanner(RetainedBytes, DiscardedBytes, RetainedOutputPortion.Tail);
    /// <summary>Returns the bounded payload plus any required loss disclosure.</summary>
    public string Snapshot() => DiscardedBytes == 0 ? RawSnapshot() : $"{FormatBanner()}\n{RawSnapshot()}";
}

/// <summary>
/// Bounded per-stream decoder: preserves split UTF-16 scalars and discards terminal escape sequences
/// across arbitrary pipe-read boundaries before stdout and stderr are merged. No line buffering.
/// </summary>
public sealed class BackgroundOutputDecoder
{
    private enum EscapeState { Text, Escape, Csi, String, StringEscape, Intermediate }
    private EscapeState _state;
    private readonly Encoder _unicodeEncoder = Encoding.UTF8.GetEncoder();
    private bool _osc;

    /// <summary>Consumes a stream chunk; final flush replaces a dangling surrogate and drops incomplete escapes.</summary>
    public string Append(ReadOnlySpan<char> text, bool final = false)
    {
        var output = new StringBuilder(text.Length + 1);
        foreach (var c in text)
        {
            switch (_state)
            {
                case EscapeState.Escape:
                    _state = c switch
                    {
                        '[' => EscapeState.Csi,
                        ']' or 'P' or 'X' or '^' or '_' => EscapeState.String,
                        >= '\x20' and <= '\x2f' => EscapeState.Intermediate,
                        _ => EscapeState.Text,
                    };
                    _osc = c == ']';
                    continue;
                case EscapeState.Csi:
                    if (c is >= '\x40' and <= '\x7e') _state = EscapeState.Text;
                    continue;
                case EscapeState.Intermediate:
                    if (c is >= '\x30' and <= '\x7e') _state = EscapeState.Text;
                    continue;
                case EscapeState.String:
                    if (c == '\x1b') _state = EscapeState.StringEscape;
                    else if (c == '\x9c' || (_osc && c == '\x07')) _state = EscapeState.Text;
                    continue;
                case EscapeState.StringEscape:
                    _state = c == '\\' ? EscapeState.Text : c == '\x1b' ? EscapeState.StringEscape : EscapeState.String;
                    continue;
            }
            if (c == '\x1b') { _state = EscapeState.Escape; continue; }
            if (c == '\x9b') { _state = EscapeState.Csi; continue; }
            if (c == '\x9d') { _state = EscapeState.String; _osc = true; continue; }
            if (c is >= '\x80' and <= '\x9f') continue;
            output.Append(c);
        }
        // The framework's streaming encoder owns incomplete scalar state and replacement fallback.
        // It emits complete UTF-8 scalars, so merging streams cannot separate surrogate halves.
        var clean = output.ToString();
        var bytes = new byte[Encoding.UTF8.GetMaxByteCount(clean.Length + 1)];
        var count = _unicodeEncoder.GetBytes(clean.AsSpan(), bytes, flush: final);
        return Encoding.UTF8.GetString(bytes, 0, count);
    }
}
