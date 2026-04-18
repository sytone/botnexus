using System.Diagnostics;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Tools;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Tools;

public sealed class FileWatcherToolTests : IDisposable
{
    private readonly List<string> _pathsToDelete = [];

    [Fact]
    public void FileWatcherTool_HasCorrectNameAndLabel()
    {
        var tool = CreateTool();

        tool.Name.Should().Be("watch_file");
        tool.Label.Should().Be("Watch File");
    }

    [Fact]
    public async Task FileWatcherTool_DetectsFileModification()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "watched.txt");
        await File.WriteAllTextAsync(path, "initial");

        var watchTask = ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = 5
        });

        await Task.Delay(TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(path, "updated");

        var result = await watchTask;
        ReadText(result).Should().Contain("File modified:");
    }

    [Fact]
    public async Task FileWatcherTool_DetectsFileCreation()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "created.txt");

        var watchTask = ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "created",
            ["timeout"] = 5
        });

        await Task.Delay(TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(path, "created");

        var result = await watchTask;
        ReadText(result).Should().Contain("File created:");
    }

    [Fact]
    public async Task FileWatcherTool_DetectsFileDeletion()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "deleted.txt");
        await File.WriteAllTextAsync(path, "delete me");

        var watchTask = ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "deleted",
            ["timeout"] = 5
        });

        await Task.Delay(TimeSpan.FromSeconds(1));
        File.Delete(path);

        var result = await watchTask;
        ReadText(result).Should().Contain("File deleted:");
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

        ReadText(result).Should().Contain("Timeout after 2 seconds");
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

        var act = () => ExecuteAsync(
            tool,
            new Dictionary<string, object?>
            {
                ["path"] = path,
                ["event"] = "modified",
                ["timeout"] = 10
            },
            cts.Token);

        var result = await act.Should().NotThrowAsync();
        ReadText(result.Subject).ToLowerInvariant().Should().Contain("cancel");
    }

    [Fact]
    public async Task FileWatcherTool_RequiresPath()
    {
        var tool = CreateTool();

        var act = () => tool.PrepareArgumentsAsync(new Dictionary<string, object?>());

        await act.Should().ThrowAsync<ArgumentException>();
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

        stopwatch.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(1800);
        stopwatch.ElapsedMilliseconds.Should().BeLessThan(4000);
        ReadText(result).Should().Contain("Timeout after 2 seconds");
    }

    [Fact]
    public async Task FileWatcherTool_ReportsElapsedTime()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "elapsed.txt");
        await File.WriteAllTextAsync(path, "initial");

        var watchTask = ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = 5
        });

        await Task.Delay(TimeSpan.FromSeconds(1));
        await File.WriteAllTextAsync(path, "updated");

        var result = await watchTask;
        ReadText(result).Should().MatchRegex(@"after \d+ seconds");
    }

    [Fact]
    public async Task FileWatcherTool_DebouncesProdRapidChanges()
    {
        var tool = CreateTool();
        var root = CreateTempDirectory();
        var path = Path.Combine(root, "debounced.txt");
        await File.WriteAllTextAsync(path, "start");

        var watchTask = ExecuteAsync(tool, new Dictionary<string, object?>
        {
            ["path"] = path,
            ["event"] = "modified",
            ["timeout"] = 5
        });

        await Task.Delay(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(path, $"change-{i}");
            await Task.Delay(40);
        }

        var result = await watchTask;
        ReadText(result).Should().Contain("File modified:");
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
        CancellationToken cancellationToken = default)
    {
        var prepared = await tool.PrepareArgumentsAsync(args, cancellationToken);
        return await tool.ExecuteAsync("call-watch-file-test", prepared, cancellationToken);
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
