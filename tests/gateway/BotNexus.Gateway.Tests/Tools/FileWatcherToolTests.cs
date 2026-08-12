using System.Diagnostics;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class FileWatcherToolTests : IDisposable
{
    private readonly List<string> _pathsToDelete = [];

    [Fact]
    public void FileWatcherTool_HasCorrectNameAndLabel()
    {
        var tool = CreateTool();

        tool.Name.ShouldBe("watch_file");
        tool.Label.ShouldBe("Watch File");
    }

    [Fact]
    public async Task FileWatcherTool_DetectsFileModification()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "watched.txt");
        await File.WriteAllTextAsync(path, "initial");

        var (watchTask, ready) = StartWatch(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = 5
        });
        await ready;
        await File.WriteAllTextAsync(path, "updated");

        var result = await watchTask;
        ReadText(result).ShouldContain("File modified:");
    }

    [Fact]
    public async Task FileWatcherTool_DetectsFileCreation()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "created.txt");

        var (watchTask, ready) = StartWatch(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "created",
            ["timeout"] = 5
        });
        await ready;
        await File.WriteAllTextAsync(path, "created");

        var result = await watchTask;
        ReadText(result).ShouldContain("File created:");
    }

    [Fact]
    public async Task FileWatcherTool_DetectsFileDeletion()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "deleted.txt");
        await File.WriteAllTextAsync(path, "delete me");

        var (watchTask, ready) = StartWatch(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "deleted",
            ["timeout"] = 5
        });
        await ready;
        File.Delete(path);

        var result = await watchTask;
        ReadText(result).ShouldContain("File deleted:");
    }

    /// <summary>
    /// Pins the ordering contract of #2988: the readiness notice must not be emitted until the watcher is
    /// actually raising events. This deletes the file synchronously ON the callback thread, so the
    /// mutation is strictly ordered between the notice and whatever the tool does next. With the notice
    /// emitted before <c>EnableRaisingEvents = true</c> the deletion is provably unobservable and the
    /// watch always times out; the assertion therefore fails 100% of the time against the unfixed code
    /// rather than merely narrowing a window.
    /// </summary>
    [Fact]
    public async Task FileWatcherTool_ReadinessNotice_IsEmittedOnlyAfterWatcherIsArmed()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "armed.txt");
        await File.WriteAllTextAsync(path, "delete me");

        var deleted = false;

        var result = await ExecuteAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["event"] = "deleted",
                ["timeout"] = 5
            },
            CancellationToken.None,
            update =>
            {
                var text = update.Content
                    .FirstOrDefault(c => c.Type == AgentToolContentType.Text)?.Value;

                if (deleted || text is null || !text.Contains("Watching '", StringComparison.Ordinal))
                    return;

                // Acting on the notice the instant it arrives is precisely what a real caller does.
                deleted = true;
                File.Delete(path);
            });

        deleted.ShouldBeTrue("the tool must emit a readiness notice");
        ReadText(result).ShouldContain("File deleted:");
    }

    [Fact]
    public async Task FileWatcherTool_TimesOut()
    {
        var tool = CreateTool(maxTimeoutSeconds: 5);
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "timeout.txt");
        await File.WriteAllTextAsync(path, "unchanged");

        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = 2
        });

        ReadText(result).ShouldContain("Timeout after 2 seconds");
    }

    [Fact]
    public async Task FileWatcherTool_CancellationReturnsInfo()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "cancelled.txt");
        await File.WriteAllTextAsync(path, "unchanged");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        var result = await ExecuteAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["event"] = "modified",
                ["timeout"] = 10
            },
            cts.Token);

        ReadText(result).ToLowerInvariant().ShouldContain("cancel");
    }

    [Fact]
    public async Task FileWatcherTool_RequiresPath()
    {
        var tool = CreateTool();

        Func<Task> act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task FileWatcherTool_ClampsTimeout()
    {
        var tool = CreateTool(maxTimeoutSeconds: 2);
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "clamped.txt");
        await File.WriteAllTextAsync(path, "unchanged");

        var stopwatch = Stopwatch.StartNew();
        var result = await ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = 999
        });

        stopwatch.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(1800);
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(4000);
        ReadText(result).ShouldContain("Timeout after 2 seconds");
    }

    [Fact]
    public async Task FileWatcherTool_ReportsElapsedTime()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "elapsed.txt");
        await File.WriteAllTextAsync(path, "initial");

        var (watchTask, ready) = StartWatch(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = 5
        });
        await ready;
        await File.WriteAllTextAsync(path, "updated");

        var result = await watchTask;
        ReadText(result).ShouldMatch(@"after \d+ seconds");
    }

    [Fact]
    public async Task FileWatcherTool_DebouncesProdRapidChanges()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "debounced.txt");
        await File.WriteAllTextAsync(path, "start");

        var (watchTask, ready) = StartWatch(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = 5
        });
        await ready;
        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(path, $"change-{i}");
            await Task.Delay(40);
        }

        var result = await watchTask;
        ReadText(result).ShouldContain("File modified:");
    }

    public void Dispose()
    {
        foreach (var path in _pathsToDelete.Where(Directory.Exists))
            Directory.Delete(path, recursive: true);
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "botnexus-file-watcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _pathsToDelete.Add(path);
        return path;
    }

    private static async Task<AgentToolResult> ExecuteAsync(
        IAgentTool tool,
        IReadOnlyDictionary<string, object?> args,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var prepared = await tool.PrepareArgumentsAsync(args, cancellationToken);
        return await tool.ExecuteAsync("call-watch-file-test", prepared, cancellationToken, onUpdate);
    }

    /// <summary>
    /// Starts a watch and returns once the tool has reported that its <see cref="FileSystemWatcher"/> is
    /// armed, so the caller can mutate the file knowing the event will be observed (#2988).
    /// </summary>
    /// <remarks>
    /// These tests previously slept for a fixed second between starting the watch and touching the file.
    /// That is a guess, not a synchronisation primitive: the watcher is armed inside the background task,
    /// so on a loaded CI runner the mutation could land BEFORE <c>EnableRaisingEvents = true</c>, the
    /// event was never raised, and the test then sat out its full timeout and failed with
    /// "Timeout after 5 seconds - no change detected". The tool already announces readiness through its
    /// onUpdate callback, which is a real happens-before edge -- use it instead of sleeping.
    /// </remarks>
    private static (Task<AgentToolResult> Watch, Task Ready) StartWatch(
        IAgentTool tool,
        IReadOnlyDictionary<string, object?> args)
    {
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var watch = ExecuteAsync(
            tool,
            args,
            CancellationToken.None,
            update =>
            {
                // The tool emits exactly one "Watching '<path>' for <event> event..." notice, and only
                // once EnableRaisingEvents is true. Anything else is ignored.
                var text = update.Content
                    .FirstOrDefault(c => c.Type == AgentToolContentType.Text)?.Value;

                if (text is not null && text.Contains("Watching '", StringComparison.Ordinal))
                    armed.TrySetResult();
            });

        // If the watch faults before ever arming, surface that instead of hanging on the gate.
        _ = watch.ContinueWith(
            t => armed.TrySetException(t.Exception!.GetBaseException()),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return (watch, armed.Task);
    }

    private static IAgentTool CreateTool(
        int? maxTimeoutSeconds = null,
        int? defaultTimeoutSeconds = null,
        int? debounceMilliseconds = null)
        => new FileWatcherTool(Options.Create(new FileWatcherToolOptions
        {
            MaxTimeoutSeconds = maxTimeoutSeconds ?? 1800,
            DefaultTimeoutSeconds = defaultTimeoutSeconds ?? 300,
            DebounceMilliseconds = debounceMilliseconds ?? 500
        }));

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
