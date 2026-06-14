using System.Diagnostics;
using System.Runtime.InteropServices;
using BotNexus.Extensions.ExecTool;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>
/// Tests for the bounded eviction of the static background-process registry in <see cref="ExecTool"/>.
/// Verifies that dead PIDs are pruned, that live background processes are retained, and that the
/// registry is capped oldest-first when it exceeds the configured size.
/// </summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public sealed class ExecToolBackgroundPruneTests : IDisposable
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private readonly List<int> _spawnedPids = [];

    public ExecToolBackgroundPruneTests()
    {
        ExecTool.ClearBackgroundProcesses();
    }

    public void Dispose()
    {
        foreach (var pid in _spawnedPids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited) p.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone — nothing to clean up.
            }
        }

        ExecTool.ClearBackgroundProcesses();
    }

    [Fact]
    public void Prune_RemovesDeadPids()
    {
        // A synthetic PID that is almost certainly not a running process.
        ExecTool.RegisterBackgroundForTest(int.MaxValue - 7, "ghost", DateTime.UtcNow);

        ExecTool.GetBackgroundProcesses().ContainsKey(int.MaxValue - 7).ShouldBeTrue();

        ExecTool.PruneBackgroundProcesses();

        ExecTool.GetBackgroundProcesses().ContainsKey(int.MaxValue - 7)
            .ShouldBeFalse("a dead PID should be pruned from the registry");
    }

    [Fact]
    public void Prune_RetainsLiveBackgroundProcess()
    {
        // Use the current test process PID as a guaranteed-live entry.
        var livePid = Environment.ProcessId;
        ExecTool.RegisterBackgroundForTest(livePid, "self", DateTime.UtcNow);
        ExecTool.RegisterBackgroundForTest(int.MaxValue - 9, "ghost", DateTime.UtcNow);

        ExecTool.PruneBackgroundProcesses();

        var map = ExecTool.GetBackgroundProcesses();
        map.ContainsKey(livePid).ShouldBeTrue("a live process must be retained");
        map.ContainsKey(int.MaxValue - 9).ShouldBeFalse("a dead PID must be pruned");
    }

    [Fact]
    public void EvictOldest_OverCap_RemovesOldestByStartTimeFirst()
    {
        var baseTime = DateTime.UtcNow;
        ExecTool.RegisterBackgroundForTest(1001, "oldest", baseTime);
        ExecTool.RegisterBackgroundForTest(1002, "middle", baseTime.AddSeconds(10));
        ExecTool.RegisterBackgroundForTest(1003, "newest", baseTime.AddSeconds(20));

        // Cap at 2 — the oldest (1001) must be evicted, the two newest retained.
        ExecTool.EvictOldestBackgroundProcesses(maxRetained: 2);

        var map = ExecTool.GetBackgroundProcesses();
        map.Count.ShouldBe(2);
        map.ContainsKey(1001).ShouldBeFalse("the oldest entry should be evicted first");
        map.ContainsKey(1002).ShouldBeTrue();
        map.ContainsKey(1003).ShouldBeTrue();
    }

    [Fact]
    public void EvictOldest_UnderCap_RetainsAll()
    {
        var baseTime = DateTime.UtcNow;
        ExecTool.RegisterBackgroundForTest(2001, "a", baseTime);
        ExecTool.RegisterBackgroundForTest(2002, "b", baseTime.AddSeconds(1));

        ExecTool.EvictOldestBackgroundProcesses(maxRetained: 10);

        ExecTool.GetBackgroundProcesses().Count.ShouldBe(2);
    }

    [Fact]
    public async Task BackgroundExecute_PrunesDeadEntriesOnRegister()
    {
        // Seed a dead PID, then launch a real short-lived background process which triggers a prune.
        ExecTool.RegisterBackgroundForTest(int.MaxValue - 11, "ghost", DateTime.UtcNow);

        var tool = new ExecTool(fileSystem: new MockFileSystem());
        string[] command = IsWindows
            ? ["cmd.exe", "/c", "ping -n 5 127.0.0.1"]
            : ["/bin/bash", "-c", "sleep 5"];

        var args = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["command"] = command,
            ["background"] = true,
        };
        var prepared = await tool.PrepareArgumentsAsync(args);
        var result = await tool.ExecuteAsync("bg-prune", prepared);

        var details = result.Details as ExecTool.ExecToolDetails;
        details.ShouldNotBeNull();
        var pid = details!.Pid;
        pid.ShouldNotBeNull();
        _spawnedPids.Add(pid!.Value);

        var map = ExecTool.GetBackgroundProcesses();
        map.ContainsKey(int.MaxValue - 11).ShouldBeFalse("registering a new background process should prune dead PIDs");
        map.ContainsKey(pid.Value).ShouldBeTrue("the freshly launched background process should be tracked");
    }
}
