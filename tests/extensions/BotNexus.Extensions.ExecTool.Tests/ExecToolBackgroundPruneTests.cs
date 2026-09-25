using System.Diagnostics;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Extensions.ExecTool.Tests;

/// <summary>Migrated metadata-pruning assertions now exercise retained handles, not unsafe PID seeding.</summary>
[Collection(ExecToolBackgroundRegistryCollection.Name)]
public sealed class ExecToolBackgroundPruneTests : IDisposable
{
    private readonly List<BackgroundProcess> _spawned = [];
    public void Dispose()
    {
        foreach (var process in _spawned) process.Dispose();
        ExecTool.ClearBackgroundProcesses();
    }

    private async Task<BackgroundProcess> Exited(DateTimeOffset startedAt)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        info.ArgumentList.Add("exit 0");
        var raw = Process.Start(info) ?? throw new InvalidOperationException("child did not start");
        var child = new BackgroundProcess(raw, "exit", startedAt);
        _spawned.Add(child);
        await child.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return child;
    }

    [Fact]
    public async Task Prune_RemovesDeadPids()
    {
        var registry = new BackgroundProcessRegistry(0);
        var child = await Exited(DateTimeOffset.UtcNow);
        registry.Register("owner", child);
        registry.Reap();
        registry.Get("owner", child.Pid).ShouldBeNull("confirmed exited child must be pruned at zero retention");
    }

    [Fact]
    public async Task Prune_RetainsLiveBackgroundProcess()
    {
        var tool = new ExecTool(null);
        var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["command"] = OperatingSystem.IsWindows()
                ? new[] { "pwsh", "-NoProfile", "-Command", "[Console]::ReadLine()" }
                : new[] { "/bin/sh", "-c", "read line" },
            ["background"] = true,
        });
        var result = await tool.ExecuteAsync("live", args);
        var details = result.Details.ShouldBeOfType<ExecTool.ExecToolDetails>();
        details.Pid.ShouldNotBeNull();
        var child = BackgroundProcessRegistry.Instance.Get(string.Empty, details.Pid.Value);
        child.ShouldNotBeNull();
        _spawned.Add(child);
        var registry = new BackgroundProcessRegistry(0);
        registry.Register("owner", child);
        var exited = await Exited(DateTimeOffset.UtcNow);
        registry.Register("owner", exited);
        registry.Reap();
        registry.Get("owner", child.Pid).ShouldBeSameAs(child, "live entries must never be evicted");
        registry.Get("owner", exited.Pid).ShouldBeNull();
    }

    [Fact]
    public async Task EvictOldest_OverCap_RemovesOldestByStartTimeFirst()
    {
        var registry = new BackgroundProcessRegistry(2);
        var time = DateTimeOffset.UtcNow;
        var oldest = await Exited(time);
        var middle = await Exited(time.AddSeconds(10));
        var newest = await Exited(time.AddSeconds(20));
        foreach (var child in new[] { oldest, middle, newest }) registry.Register("owner", child);
        var map = registry.List("owner");
        map.Count.ShouldBe(2);
        map.ShouldNotContain(oldest);
        map.ShouldContain(middle);
        map.ShouldContain(newest);
    }

    [Fact]
    public async Task EvictOldest_UnderCap_RetainsAll()
    {
        var registry = new BackgroundProcessRegistry(10);
        var first = await Exited(DateTimeOffset.UtcNow);
        var second = await Exited(DateTimeOffset.UtcNow.AddSeconds(1));
        registry.Register("owner", first);
        registry.Register("owner", second);
        registry.List("owner").Count.ShouldBe(2);
    }

    [Fact]
    public async Task BackgroundExecute_PrunesDeadEntriesOnRegister()
    {
        const int retentionCap = 1;
        const string owner = "launch-owner";
        var registry = new BackgroundProcessRegistry(retentionCap);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-retentionCap);
        var retained = new List<BackgroundProcess>(retentionCap);
        for (var index = 0; index < retentionCap; index++)
        {
            var completed = await Exited(startedAt.AddSeconds(index));
            registry.Register(owner, completed);
            retained.Add(completed);
        }

        var foreignOwner = "foreign-" + Guid.NewGuid().ToString("N");
        var foreignLive = StartLiveProcess(startedAt.AddMinutes(-1));
        registry.Register(foreignOwner, foreignLive);
        _spawned.Add(foreignLive);

        try
        {
            registry.List(owner).Count.ShouldBe(retentionCap, "the completed retention cap must be full before launch");
            var oldest = retained[0];
            registry.Get(owner, oldest.Pid).ShouldBeSameAs(oldest);

            var tool = new ExecTool(
                null,
                null,
                owner,
                registry,
                managed => managed.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            var args = await tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["command"] = OperatingSystem.IsWindows()
                    ? new[] { "cmd.exe", "/c", "exit 0" }
                    : new[] { "/bin/sh", "-c", "exit 0" },
                ["background"] = true,
            });
            var result = await tool.ExecuteAsync("prune-on-launch", args);
            var launchedPid = result.Details.ShouldBeOfType<ExecTool.ExecToolDetails>().Pid;
            launchedPid.ShouldNotBeNull();
            registry.Get(owner, oldest.Pid).ShouldBeNull("the real launch registration must evict the oldest completed entry");
            var launched = registry.Get(owner, launchedPid.Value);
            launched.ShouldNotBeNull("a real ExecTool launch must register its owned process");
            _spawned.Add(launched);
            await launched.WaitForCompletionAsync().WaitAsync(TimeSpan.FromSeconds(30));

            var afterLaunch = registry.List(owner);
            afterLaunch.Count.ShouldBe(retentionCap, "two completed registrations must be pruned back to the cap of one");
            afterLaunch.ShouldNotContain(oldest, "the oldest completed entry must be evicted after the real launch completes");
            afterLaunch.ShouldContain(launched, "the newly launched process must occupy the retained slot");
            registry.Get(foreignOwner, foreignLive.Pid).ShouldBeSameAs(foreignLive, "another owner's live process must remain protected");
        }
        finally
        {
            foreignLive.Kill();
            registry.Clear(foreignOwner);
        }
    }

    private static BackgroundProcess StartLiveProcess(DateTimeOffset startedAt)
    {
        var info = new ProcessStartInfo(OperatingSystem.IsWindows() ? "pwsh" : "/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add("[Console]::ReadLine()");
        }
        else
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("read line");
        }

        var raw = Process.Start(info) ?? throw new InvalidOperationException("live child did not start");
        return new BackgroundProcess(raw, "live", startedAt);
    }
}
