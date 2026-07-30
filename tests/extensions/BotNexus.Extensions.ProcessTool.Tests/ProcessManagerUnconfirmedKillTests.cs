using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.ProcessTool.Tests;

/// <summary>
/// Tests for issue #2521: a tree kill whose exit was never confirmed must NOT release the
/// registration slot. The observable contract is that the entry stays visible in the registry,
/// survives reaping, remains re-killable, and produces a Warning naming the PID and process name.
/// </summary>
public sealed class ProcessManagerUnconfirmedKillTests : IDisposable
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

    /// <summary>
    /// A <see cref="ManagedProcess"/> whose underlying termination never confirms exit, standing in
    /// for a Windows tree kill that left a descendant alive. Exercises the real state bookkeeping.
    /// </summary>
    private sealed class UnconfirmedKillProcess(Process process, string command, DateTimeOffset startedAt)
        : ManagedProcess(process, command, startedAt)
    {
        public int KillAttempts { get; private set; }

        protected override bool KillCore()
        {
            KillAttempts++;
            return false;
        }
    }

    private sealed class ConfirmedKillProcess(Process process, string command, DateTimeOffset startedAt)
        : ManagedProcess(process, command, startedAt)
    {
        protected override bool KillCore() => true;
    }

    private sealed record LogLine(LogLevel Level, string Message);

    private sealed class RecordingLogger : ILogger
    {
        public List<LogLine> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Lines.Add(new LogLine(logLevel, formatter(state, exception)));
    }

    private ProcessManager NewManager(int maxExitedRetained, ILogger? logger = null)
    {
        var manager = new ProcessManager(maxExitedRetained, logger);
        _managers.Add(manager);
        return manager;
    }

    private static ProcessStartInfo ExitNowStartInfo() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c exit /b 0", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }
        : new ProcessStartInfo { FileName = "/bin/bash", Arguments = "-c \"exit 0\"", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

    private T Spawn<T>(Func<Process, T> factory) where T : ManagedProcess
    {
        var process = Process.Start(ExitNowStartInfo())!;
        var managed = factory(process);
        _spawned.Add(managed);
        managed.WaitForExit(5_000);
        return managed;
    }

    [Fact]
    public void UnconfirmedKill_KeepsRegistrationVisibleAndReKillable()
    {
        // Cap of zero: anything eligible for eviction is dropped on the next reap.
        var manager = NewManager(maxExitedRetained: 0);
        var managed = Spawn(p => new UnconfirmedKillProcess(p, "unconfirmed", DateTimeOffset.UtcNow));
        manager.Register(managed.Pid, managed);

        manager.Kill(managed.Pid).ShouldBeTrue("the PID was registered, so the kill request is accepted");

        manager.Reap();

        // Observable 1: the registration is still present after a reap that would otherwise evict it.
        manager.Get(managed.Pid).ShouldNotBeNull("an unconfirmed kill must not release the registration slot");

        // Observable 2: it is still visible to `process list`.
        manager.List().Select(p => p.Pid).ShouldContain(managed.Pid);

        // Observable 3: it is still re-killable - a second kill reaches the process again.
        manager.Kill(managed.Pid).ShouldBeTrue();
        managed.KillAttempts.ShouldBe(2, "the retained entry must remain re-killable");

        // Observable 4: it was not disposed out from under us.
        manager.Get(managed.Pid).ShouldBeSameAs(managed);
    }

    [Fact]
    public void UnconfirmedKill_LogsWarningWithPidAndProcessName()
    {
        var logger = new RecordingLogger();
        var manager = NewManager(maxExitedRetained: 0, logger);
        var managed = Spawn(p => new UnconfirmedKillProcess(p, "unconfirmed", DateTimeOffset.UtcNow));
        manager.Register(managed.Pid, managed);

        manager.Kill(managed.Pid);

        var warnings = logger.Lines.Where(l => l.Level == LogLevel.Warning).ToList();
        warnings.ShouldNotBeEmpty("an unconfirmed tree kill must not be silent");
        warnings.ShouldContain(l => l.Message.Contains(managed.Pid.ToString()));
        warnings.ShouldContain(l => l.Message.Contains(managed.ProcessName));
    }

    [Fact]
    public void ConfirmedKill_ReleasesSlotAsBefore()
    {
        var logger = new RecordingLogger();
        var manager = NewManager(maxExitedRetained: 0, logger);
        var managed = Spawn(p => new ConfirmedKillProcess(p, "confirmed", DateTimeOffset.UtcNow));
        manager.Register(managed.Pid, managed);

        manager.Kill(managed.Pid).ShouldBeTrue();

        manager.Reap();

        // Happy path: a confirmed termination still releases the slot - no regression in reaping.
        manager.Get(managed.Pid).ShouldBeNull("a confirmed kill must still release the registration slot");
        logger.Lines.ShouldNotContain(l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public void KillState_TracksConfirmationOutcome()
    {
        var unconfirmed = Spawn(p => new UnconfirmedKillProcess(p, "unconfirmed", DateTimeOffset.UtcNow));
        var confirmed = Spawn(p => new ConfirmedKillProcess(p, "confirmed", DateTimeOffset.UtcNow));

        unconfirmed.KillState.ShouldBe(ProcessKillState.NotRequested);

        unconfirmed.Kill().ShouldBeFalse();
        confirmed.Kill().ShouldBeTrue();

        unconfirmed.KillState.ShouldBe(ProcessKillState.Unconfirmed);
        confirmed.KillState.ShouldBe(ProcessKillState.Confirmed);
    }

    [Fact]
    public void Kill_UnknownPid_ReturnsFalse()
    {
        var manager = NewManager(maxExitedRetained: 0);
        manager.Kill(-424242).ShouldBeFalse();
    }
}
