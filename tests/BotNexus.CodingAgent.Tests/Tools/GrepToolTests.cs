using BotNexus.CodingAgent.Tools;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class GrepToolTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-greptool-{Guid.NewGuid():N}");
    private readonly GrepTool _tool;

    public GrepToolTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _tool = new GrepTool(_tempDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPatternMatches_ReturnsMatchingLinesWithFileAndLine()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "sample.txt"), "alpha\nneedle hit\ngamma");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = "needle"
        });

        result.Content.Should().ContainSingle();
        result.Content[0].Value.Should().Contain($"sample.txt:2: needle hit");
    }

    [Fact]
    public async Task ExecuteAsync_WhenRegexPatternUsed_ReturnsRegexMatches()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "regex.txt"), "foo1\nfoo2\nbar");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = "foo\\d"
        });

        result.Content[0].Value.Should().Contain("regex.txt:1: foo1");
        result.Content[0].Value.Should().Contain("regex.txt:2: foo2");
        result.Content[0].Value.Should().NotContain("bar");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMaxResultsProvided_TruncatesOutput()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "many.txt"), "match\nmatch\nmatch\nmatch");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = "match",
            ["max_results"] = 2
        });

        var lines = result.Content[0].Value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Count(line => line.StartsWith("many.txt:", StringComparison.Ordinal)).Should().Be(2);
        result.Content[0].Value.Should().Contain("[warning] Results truncated at 2 matches.");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathMissing_ReturnsFriendlyMessage()
    {
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = "anything",
            ["path"] = "missing-directory"
        });

        result.Content[0].Value.Should().Contain("does not exist");
    }

    [Fact]
    public async Task ExecuteAsync_WhenIncludeProvided_FiltersFiles()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "file.cs"), "target");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "file.txt"), "target");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = "target",
            ["include"] = "*.cs"
        });

        result.Content[0].Value.Should().Contain("file.cs:1: target");
        result.Content[0].Value.Should().NotContain("file.txt");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
