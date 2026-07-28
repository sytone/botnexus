using System.Diagnostics;

namespace BotNexus.Extensions.ProcessTool.Tests;

/// <summary>
/// Regression tests for issue #2479: <see cref="ManagedProcess.Start"/> must honour cancellation
/// immediately before <c>Process.Start()</c>, and must kill the whole tree (returning nothing to
/// register) when cancellation lands after the child is already live.
/// </summary>
public class ManagedProcessCancellationTests
{
    [Fact]
    public void Start_TokenAlreadyCancelled_ThrowsAndSpawnsNothingAndRegistryStaysEmpty()
    {
        var manager = new ProcessManager();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var startedCount = 0;

        Should.Throw<OperationCanceledException>(() =>
        {
            var managed = ManagedProcess.Start(
                LongRunningStartInfo(),
                "long-runner",
                cts.Token,
                _ => startedCount++);
            manager.Register(managed.Pid, managed);
        });

        // Observables a broken implementation would move: no OS process created, nothing registered.
        startedCount.ShouldBe(0);
        manager.List().ShouldBeEmpty();
    }

    [Fact]
    public void Start_CancelledAfterProcessStarted_KillsTreeAndRegistersNothing()
    {
        var manager = new ProcessManager();
        using var cts = new CancellationTokenSource();
        var pid = 0;

        Should.Throw<OperationCanceledException>(() =>
        {
            var managed = ManagedProcess.Start(
                LongRunningStartInfo(),
                "long-runner",
                cts.Token,
                p =>
                {
                    pid = p.Id;
                    cts.Cancel();
                });
            manager.Register(managed.Pid, managed);
        });

        pid.ShouldBeGreaterThan(0);

        // Nothing registered - the orphan cannot count against the background cap or outlive its turn.
        manager.List().ShouldBeEmpty();
        manager.Get(pid).ShouldBeNull();

        // And the live child was actually torn down via the entireProcessTree kill path.
        WaitForPidGone(pid).ShouldBeTrue($"PID {pid} survived a cancelled start (orphan leak).");
    }

    [Fact]
    public void Start_NotCancelled_ReturnsRunningManagedProcessThatRegisters()
    {
        // Guards against a vacuous fix that never starts or never returns a process.
        var manager = new ProcessManager();

        var managed = ManagedProcess.Start(LongRunningStartInfo(), "long-runner", CancellationToken.None);
        try
        {
            managed.Pid.ShouldBeGreaterThan(0);
            managed.IsRunning.ShouldBeTrue();

            manager.Register(managed.Pid, managed);
            manager.Get(managed.Pid).ShouldNotBeNull();
            manager.List().Count.ShouldBe(1);
        }
        finally
        {
            manager.Clear();
        }
    }

    private static ProcessStartInfo LongRunningStartInfo() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping -n 60 127.0.0.1",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }
        : new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c \"sleep 60\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

    private static bool WaitForPidGone(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid))
                return true;
            Thread.Sleep(100);
        }

        return !IsAlive(pid);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
