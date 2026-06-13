using System.Diagnostics;
using BotNexus.Extensions.ProcessTool;

namespace BotNexus.Extensions.ProcessTool.Tests;

/// <summary>
/// Tests for the bounded eviction (reaping) of exited processes in <see cref="ProcessManager"/>.
/// Verifies that exited processes beyond the retention cap are evicted oldest-first, that running
/// processes are never evicted, and that under-cap registries retain everything.
/// </summary>
public sealed class ProcessManagerReapTests : IDisposable
{
    private readonly List<ManagedProcess> _spawned = [];
    private readonly List<ProcessManager> _managers = [];

    public void Dispose()
    {
        foreach (var m in _managers)
            m.Clear();
        foreach (var p in _spawned)
            p.Dispose();
    }

    private ProcessManager NewManager(int maxExitedRetained)
    {
        var manager = new ProcessManager(maxExitedRetained);
        _managers.Add(manager);
        return manager;
    }

    /// <summary>Spawns a process that exits immediately and registers it with the given manager.</summary>
    private ManagedProcess RegisterExited(ProcessManager manager, DateTimeOffset startedAt)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c exit /b 0", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }
            : new ProcessStartInfo { FileName = "/bin/bash", Arguments = "-c \"exit 0\"", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

        var process = Process.Start(psi)!;
        var managed = new ManagedProcess(process, "exit-now", startedAt);
        _spawned.Add(managed);
        managed.WaitForExit(5_000);
        manager.Register(process.Id, managed);
        return managed;
    }

    /// <summary>Spawns a long-running process and registers it (does not wait for exit).</summary>
    private ManagedProcess RegisterRunning(ProcessManager manager)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c ping -n 60 127.0.0.1", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }
            : new ProcessStartInfo { FileName = "/bin/bash", Arguments = "-c \"sleep 60\"", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

        var process = Process.Start(psi)!;
        var managed = new ManagedProcess(process, "long-runner", DateTimeOffset.UtcNow);
        _spawned.Add(managed);
        manager.Register(process.Id, managed);
        return managed;
    }

    [Fact]
    public void Register_UnderCap_RetainsAllExitedProcesses()
    {
        var manager = NewManager(maxExitedRetained: 5);

        var baseTime = DateTimeOffset.UtcNow;
        var p1 = RegisterExited(manager, baseTime);
        var p2 = RegisterExited(manager, baseTime.AddSeconds(1));
        var p3 = RegisterExited(manager, baseTime.AddSeconds(2));

        var listed = manager.List().Select(p => p.Pid).ToHashSet();

        listed.ShouldContain(p1.Pid);
        listed.ShouldContain(p2.Pid);
        listed.ShouldContain(p3.Pid);
        listed.Count.ShouldBe(3);
    }

    [Fact]
    public void Register_OverCap_EvictsOldestExitedFirst()
    {
        var manager = NewManager(maxExitedRetained: 2);

        var baseTime = DateTimeOffset.UtcNow;
        var oldest = RegisterExited(manager, baseTime);                 // should be evicted
        var middle = RegisterExited(manager, baseTime.AddSeconds(10));  // retained
        var newest = RegisterExited(manager, baseTime.AddSeconds(20));  // retained (triggers eviction of oldest)

        var listed = manager.List().Select(p => p.Pid).ToHashSet();

        listed.Count.ShouldBe(2);
        listed.ShouldNotContain(oldest.Pid);
        listed.ShouldContain(middle.Pid);
        listed.ShouldContain(newest.Pid);
    }

    [Fact]
    public void Reap_NeverEvictsRunningProcesses()
    {
        // Cap of zero means: keep no exited processes, but running ones must survive.
        var manager = NewManager(maxExitedRetained: 0);

        var running = RegisterRunning(manager);
        try
        {
            // Add several exited processes; each Register triggers a reap.
            var baseTime = DateTimeOffset.UtcNow;
            RegisterExited(manager, baseTime);
            RegisterExited(manager, baseTime.AddSeconds(1));

            manager.Reap();

            var listed = manager.List().Select(p => p.Pid).ToHashSet();
            listed.ShouldContain(running.Pid, "a running process must never be evicted");
            // With a zero cap, all exited processes are reaped away.
            listed.Count.ShouldBe(1);
        }
        finally
        {
            running.Kill();
        }
    }

    [Fact]
    public void Reap_DisposesEvictedProcesses()
    {
        var manager = NewManager(maxExitedRetained: 1);

        var baseTime = DateTimeOffset.UtcNow;
        var evicted = RegisterExited(manager, baseTime);
        RegisterExited(manager, baseTime.AddSeconds(5)); // triggers eviction of `evicted`

        // The evicted process should no longer be tracked.
        manager.Get(evicted.Pid).ShouldBeNull();

        // Disposed ManagedProcess throws on input writes (proves Dispose ran / handle released).
        Should.Throw<ObjectDisposedException>(() => evicted.WriteInput("x"));
    }

    [Fact]
    public void Constructor_NegativeCap_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new ProcessManager(-1));
    }
}
