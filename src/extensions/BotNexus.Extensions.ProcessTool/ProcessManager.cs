using BotNexus.Agent.Core.Tools;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.ProcessTool;

/// <summary>Compatibility facade; the host-shared core registry owns every child and its output.</summary>
public sealed class ProcessManager
{
    internal const int DefaultMaxExitedRetained = 100;
    /// <summary>Standalone tool context; contributed tools use their immutable agent owner key.</summary>
    public static ProcessManager Instance { get; } = new(BackgroundProcessRegistry.Instance, string.Empty);
    private readonly BackgroundProcessRegistry _registry;
    private readonly string _owner;
    private readonly ILogger? _logger;

    /// <summary>Creates an isolated registry for an independent host.</summary>
    public ProcessManager() : this(DefaultMaxExitedRetained, null) { }
    internal ProcessManager(int maxExitedRetained) : this(maxExitedRetained, null) { }
    internal ProcessManager(int maxExitedRetained, ILogger? logger)
        : this(new BackgroundProcessRegistry(maxExitedRetained), string.Empty) => _logger = logger;
    internal ProcessManager(BackgroundProcessRegistry registry, string owner)
    {
        _registry = registry;
        _owner = owner;
    }
    /// <summary>Registers only a wrapper whose actual PID matches the supplied handle identity.</summary>
    public void Register(int pid, ManagedProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (pid != process.Pid) throw new ArgumentException("PID must match the owned process.", nameof(pid));
        _registry.Register(_owner, process);
    }
    /// <summary>Looks up this owner's child without attaching to arbitrary host processes.</summary>
    public BackgroundProcess? Get(int pid) => _registry.Get(_owner, pid);
    /// <summary>Lists this owner's retained processes after bounded completed-entry cleanup.</summary>
    public IReadOnlyList<ManagedProcessInfo> List() => _registry.List(_owner)
        .Select(p => new ManagedProcessInfo(p.Pid, p.Command, p.IsRunning, p.StartedAt, p.ExitCode)).ToArray();
    /// <summary>Requests termination without discarding an unconfirmed registration.</summary>
    public bool Kill(int pid)
    {
        var process = Get(pid);
        if (process is null) return false;
        if (!process.Kill())
            _logger?.LogWarning("Tree kill of process {Pid} ({ProcessName}) was not confirmed; keeping registration.", process.Pid, process.ProcessName);
        return true;
    }
    internal void Reap() => _registry.Reap();
    internal void Clear() => _registry.Clear(_owner);
    internal bool Remove(int pid) => _registry.Remove(_owner, pid);
}

/// <summary>Immutable status projection without granting access to process handles.</summary>
public sealed record ManagedProcessInfo(int Pid, string Command, bool IsRunning, DateTimeOffset StartedAt, int? ExitCode);
