using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.ProcessTool.Tests;

/// <summary>
/// Unconfirmed tree kills pin running roots, but exited roots become bounded unresolved diagnostics.
/// No cleanup or retry may turn uncertainty into a successful termination.
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
        private readonly Process _raw = process;

        public int KillAttempts { get; private set; }

        // Stands in for a Windows tree kill where the root died but a descendant outlived the wait:
        // the OS process is genuinely gone (so it is reap-eligible) yet exit was never CONFIRMED.
        protected override bool KillCore()
        {
            KillAttempts++;
            try { if (!_raw.HasExited) _raw.Kill(entireProcessTree: true); } catch { /* best effort */ }
            try { _raw.WaitForExit(5_000); } catch { /* best effort */ }
            return false;
        }
    }

    private sealed class ConfirmedKillProcess(Process process, string command, DateTimeOffset startedAt)
        : ManagedProcess(process, command, startedAt)
    {
        private readonly Process _raw = process;

        protected override bool KillCore()
        {
            try { if (!_raw.HasExited) _raw.Kill(entireProcessTree: true); } catch { /* best effort */ }
            try { _raw.WaitForExit(5_000); } catch { /* best effort */ }
            return true;
        }
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

    private static ProcessStartInfo LongRunnerStartInfo() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo { FileName = "cmd.exe", Arguments = "/c ping -n 60 127.0.0.1", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }
        : new ProcessStartInfo { FileName = "/bin/bash", Arguments = "-c \"sleep 60\"", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };

    /// <summary>Spawns a live process (so registration is not reaped before the kill under test).</summary>
    private T Spawn<T>(Func<Process, T> factory) where T : ManagedProcess
    {
        var process = Process.Start(LongRunnerStartInfo())!;
        var managed = factory(process);
        _spawned.Add(managed);
        return managed;
    }

    [Fact]
    public async Task ExitedUnconfirmedKill_IsBoundedAndCannotBeReKilled()
    {
        var manager = NewManager(maxExitedRetained: 1);
        var managed = Spawn(p => new UnconfirmedKillProcess(p, "unconfirmed", DateTimeOffset.UtcNow));
        manager.Register(managed.Pid, managed);

        manager.Kill(managed.Pid).ShouldBeFalse("accepting a request is not confirmed termination");
        await managed.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        manager.Reap();
        manager.Get(managed.Pid).ShouldBeSameAs(managed);
        var info = manager.List().ShouldHaveSingleItem();
        info.Pid.ShouldBe(managed.Pid);
        info.IsRunning.ShouldBeFalse();
        info.KillUnconfirmed.ShouldBeTrue();
        info.Status.ShouldContain("unresolved");
        info.Status.ShouldContain("operator intervention");
        manager.Kill(managed.Pid).ShouldBeFalse();
        managed.KillAttempts.ShouldBe(1, "an exited root cannot safely be used to find descendants again");
        managed.IsComplete.ShouldBeFalse();
        manager.Clear();
        manager.Get(managed.Pid).ShouldBeNull("bounded diagnostics can be explicitly released");
        managed.KillState.ShouldBe(ProcessKillState.Unconfirmed);
    }

    private sealed class RunningUnconfirmedKillProcess : ManagedProcess
    {
        private readonly Process _root;
        public RunningUnconfirmedKillProcess(Process process)
            : base(process, "running-unconfirmed", DateTimeOffset.UtcNow) => _root = process;
        public int Attempts { get; private set; }
        protected override bool KillCore() { Attempts++; return false; }
        public void ExitRoot()
        {
            _root.Kill(entireProcessTree: true);
            _root.WaitForExit(5_000).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task RunningUnconfirmedKill_StaysVisibleAndReKillableUntilExit()
    {
        var manager = NewManager(maxExitedRetained: 0);
        var managed = Spawn(p => new RunningUnconfirmedKillProcess(p));
        manager.Register(managed.Pid, managed);
        try
        {
            manager.Kill(managed.Pid).ShouldBeFalse();
            manager.Reap();
            manager.Clear();
            manager.Get(managed.Pid).ShouldBeSameAs(managed);
            manager.List().ShouldHaveSingleItem().IsRunning.ShouldBeTrue();
            manager.Kill(managed.Pid).ShouldBeFalse();
            managed.Attempts.ShouldBeGreaterThanOrEqualTo(2);
        }
        finally { managed.ExitRoot(); }
        await managed.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        manager.Reap();
        manager.Get(managed.Pid).ShouldBeNull();
        managed.KillState.ShouldBe(ProcessKillState.Unconfirmed);
    }

    [Fact]
    public async Task ProcessTool_ExitedUnconfirmedKill_ReportsUnresolvedAcrossAllActions()
    {
        var manager = NewManager(maxExitedRetained: 1);
        var managed = Spawn(p => new UnconfirmedKillProcess(p, "unconfirmed", DateTimeOffset.UtcNow));
        manager.Register(managed.Pid, managed);
        var tool = new ProcessTool(manager);
        async Task<string> Call(string action)
        {
            var result = await tool.ExecuteAsync("call", new Dictionary<string, object?>
            {
                ["action"] = action, ["pid"] = managed.Pid,
            });
            return string.Join("", result.Content.Select(c => c.Value));
        }
        var firstKill = await Call("kill");
        firstKill.ShouldContain("did NOT confirm termination");
        firstKill.ShouldContain("Operator intervention");
        firstKill.ShouldNotContain("can be killed again");
        await managed.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var action in new[] { "status", "list", "output" })
        {
            var text = await Call(action);
            text.ShouldContain("unresolved");
            text.ShouldContain("operator intervention");
        }
        var retry = await Call("kill");
        retry.ShouldContain("unresolved");
        retry.ShouldNotContain("already exited");
        retry.ShouldNotContain("terminated (exit code");
        retry.ShouldNotContain("can be killed again");
        managed.KillAttempts.ShouldBe(1);
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
        confirmed.KillState.ShouldBe(ProcessKillState.NotRequested);

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
