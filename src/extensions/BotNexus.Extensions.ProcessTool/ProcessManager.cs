using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>
/// Static shared registry for tracked background processes.
/// Any tool or extension can register processes here for lifecycle management.
/// </summary>
/// <remarks>
/// The registry bounds the number of *exited* processes it retains so that a long-running
/// gateway that launches many short-lived background processes does not leak <see cref="ManagedProcess"/>
/// instances (each of which pins an OS process handle plus a captured output buffer) indefinitely.
/// Running processes are never evicted — only completed ones beyond the retention cap, oldest first.
/// </remarks>
public sealed class ProcessManager
{
    /// <summary>
    /// Maximum number of *exited* processes kept in the registry. When the count of exited
    /// processes exceeds this cap, the oldest exited entries are evicted and disposed.
    /// Running processes do not count against this cap and are never evicted.
    /// </summary>
    internal const int DefaultMaxExitedRetained = 100;

    /// <summary>Global singleton instance shared across extensions.</summary>
    public static ProcessManager Instance { get; } = new();

    private readonly ConcurrentDictionary<int, ManagedProcess> _processes = new();
    private readonly int _maxExitedRetained;
    private readonly ILogger? _logger;

    /// <summary>Creates a registry with the default exited-process retention cap.</summary>
    public ProcessManager() : this(DefaultMaxExitedRetained, logger: null) { }

    /// <summary>Creates a registry with an explicit exited-process retention cap (primarily for tests).</summary>
    /// <param name="maxExitedRetained">Maximum number of exited processes to retain; must be &gt;= 0.</param>
    internal ProcessManager(int maxExitedRetained) : this(maxExitedRetained, logger: null) { }

    /// <summary>Creates a registry with an explicit retention cap and a logger for unconfirmed kills.</summary>
    internal ProcessManager(int maxExitedRetained, ILogger? logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxExitedRetained);
        _maxExitedRetained = maxExitedRetained;
        _logger = logger;
    }

    public void Register(int pid, ManagedProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _processes[pid] = process;

        // Opportunistically reap completed processes so the registry stays bounded.
        Reap();
    }

    public ManagedProcess? Get(int pid)
        => _processes.TryGetValue(pid, out var p) ? p : null;

    public IReadOnlyList<ManagedProcessInfo> List()
    {
        // Reap on read too, so `process list` never surfaces an unbounded backlog of exited entries.
        Reap();

        return _processes.Values
            .Select(p => new ManagedProcessInfo(p.Pid, p.Command, p.IsRunning, p.StartedAt, p.ExitCode))
            .ToList();
    }

    /// <summary>
    /// Requests termination of a tracked process. Returns <c>true</c> when the PID was tracked and a
    /// kill was attempted. The registration is only released once termination is <em>confirmed</em>;
    /// an unconfirmed kill keeps the entry visible and re-killable (and is logged at Warning), because
    /// the process or its descendants may still be running.
    /// </summary>
    public bool Kill(int pid)
    {
        if (!_processes.TryGetValue(pid, out var process))
            return false;

        if (!process.Kill())
        {
            _logger?.LogWarning(
                "Tree kill of process {Pid} ({ProcessName}) was not confirmed within the grace period; " +
                "descendants may still be running. Keeping the registration so it stays visible and re-killable.",
                process.Pid,
                process.ProcessName);
        }

        return true;
    }

    /// <summary>
    /// Evicts exited processes beyond the retention cap, oldest first (by start time), and disposes
    /// each evicted instance so its OS process handle and event subscriptions are released.
    /// Running processes are never evicted. Safe to call concurrently.
    /// </summary>
    internal void Reap()
    {
        // Snapshot the exited entries. ConcurrentDictionary enumeration is safe under concurrent mutation.
        // An unconfirmed kill may have left live descendants behind, so those entries are pinned:
        // evicting (and disposing) them would drop the only handle and make the orphan untrackable.
        var exited = _processes.Values
            .Where(p => !p.IsRunning && p.KillState != ProcessKillState.Unconfirmed)
            .OrderBy(p => p.StartedAt)
            .ToList();

        var evictCount = exited.Count - _maxExitedRetained;
        if (evictCount <= 0)
            return;

        for (var i = 0; i < evictCount; i++)
        {
            var process = exited[i];
            if (_processes.TryRemove(process.Pid, out var removed))
                removed.Dispose();
        }
    }

    /// <summary>Removes all tracked processes. Intended for testing only.</summary>
    internal void Clear()
    {
        foreach (var kvp in _processes)
            kvp.Value.Dispose();

        _processes.Clear();
    }

    internal bool Remove(int pid) => _processes.TryRemove(pid, out _);
}

/// <summary>Snapshot of a managed process for list results.</summary>
public sealed record ManagedProcessInfo(
    int Pid,
    string Command,
    bool IsRunning,
    DateTimeOffset StartedAt,
    int? ExitCode);
