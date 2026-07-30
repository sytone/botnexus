using System.Diagnostics;
using BotNexus.Agent.Core.Types;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Regression tests for issue #2479: a CancellationToken cancelled between the tool's entry check and
/// <c>Process.Start()</c> must not leave a live orphan child registered in the background registry.
/// </summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public class ExecToolCancellationTests : IDisposable
{
    private readonly ExecTool _tool = new(workingDirectory: null, fileSystem: new MockFileSystem());

    public void Dispose()
    {
        ExecTool.StartedTestHook = null;
        ExecTool.ClearBackgroundProcesses();
    }

    [Fact]
    public async Task ExecuteAsync_TokenCancelledBeforeStart_DoesNotSpawnOrRegisterAnyProcess()
    {
        ExecTool.ClearBackgroundProcesses();

        var started = 0;
        ExecTool.StartedTestHook = _ => Interlocked.Increment(ref started);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _tool.ExecuteAsync("cancel-before", BuildBackgroundArgs(), cts.Token));

        // Observable that a broken implementation would move: no OS process was created at all,
        // and the background registry is untouched so no slot is consumed.
        started.ShouldBe(0);
        ExecTool.GetBackgroundProcesses().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_TokenCancelledAfterStart_KillsTreeAndLeavesRegistryEmpty()
    {
        ExecTool.ClearBackgroundProcesses();

        using var cts = new CancellationTokenSource();
        var pid = 0;

        // Cancel in the window between Process.Start() and the post-start check. This is the exact race
        // #2479 describes: the child is already live when cancellation is observed.
        ExecTool.StartedTestHook = p =>
        {
            pid = p.Id;
            cts.Cancel();
        };

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _tool.ExecuteAsync("cancel-after", BuildBackgroundArgs(), cts.Token));

        pid.ShouldBeGreaterThan(0);

        // 1. The process was never registered - it cannot count against MaxBackgroundProcesses.
        ExecTool.GetBackgroundProcesses().ShouldNotContainKey(pid);
        ExecTool.GetBackgroundProcesses().ShouldBeEmpty();

        // 2. The child is dead, not an orphan outliving its turn.
        WaitForPidGone(pid).ShouldBeTrue($"PID {pid} was still alive after a cancelled start (orphan leak).");
    }

    [Fact]
    public async Task ExecuteAsync_BackgroundNotCancelled_StillRegistersProcess()
    {
        // Guards against a vacuous fix that simply stopped registering background processes.
        ExecTool.ClearBackgroundProcesses();

        var result = await _tool.ExecuteAsync("normal", BuildBackgroundArgs(), CancellationToken.None);

        result.Content.ShouldNotBeEmpty();
        var registered = ExecTool.GetBackgroundProcesses();
        registered.ShouldNotBeEmpty();

        foreach (var pid in registered.Keys.ToList())
            TryKillPid(pid);
    }

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

    private static void TryKillPid(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    private static IReadOnlyDictionary<string, object?> BuildBackgroundArgs()
    {
        string[] command = OperatingSystem.IsWindows()
            ? ["cmd.exe", "/c", "ping -n 60 127.0.0.1"]
            : ["/bin/sh", "-c", "sleep 60"];

        return new Dictionary<string, object?>
        {
            ["command"] = (IReadOnlyList<string>)command.ToList(),
            ["timeoutMs"] = 120_000,
            ["noOutputTimeoutMs"] = null,
            ["input"] = null,
            ["background"] = true,
            ["env"] = null,
            ["workingDir"] = null,
        };
    }
}
