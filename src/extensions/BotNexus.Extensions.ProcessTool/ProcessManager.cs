using System.Collections.Concurrent;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>
/// Static shared registry for tracked background processes.
/// Any tool or extension can register processes here for lifecycle management.
/// </summary>
public sealed class ProcessManager
{
    /// <summary>Global singleton instance shared across extensions.</summary>
    public static ProcessManager Instance { get; } = new();

    private readonly ConcurrentDictionary<int, ManagedProcess> _processes = new();

    public void Register(int pid, ManagedProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _processes[pid] = process;
    }

    public ManagedProcess? Get(int pid)
        => _processes.TryGetValue(pid, out var p) ? p : null;

    public IReadOnlyList<ManagedProcessInfo> List()
        => _processes.Values
            .Select(p => new ManagedProcessInfo(p.Pid, p.Command, p.IsRunning, p.StartedAt, p.ExitCode))
            .ToList();

    public bool Kill(int pid)
    {
        if (!_processes.TryGetValue(pid, out var process))
            return false;

        process.Kill();
        return true;
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
