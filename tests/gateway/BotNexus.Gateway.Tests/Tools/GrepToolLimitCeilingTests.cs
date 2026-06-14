using BotNexus.Tools;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// Covers the upper-bound clamp on GrepTool's <c>limit</c>/<c>max_results</c> argument. Without a ceiling,
/// the agent-supplied value flows into <c>new List&lt;string&gt;(capacity: maxResults)</c> and a multi-billion
/// value throws <see cref="OutOfMemoryException"/>/<see cref="ArgumentOutOfRangeException"/> before any
/// output-size protection runs. See issue #1358.
/// </summary>
public sealed class GrepToolLimitCeilingTests : IDisposable
{
    private const int MaxLimit = 1000;

    private readonly string _tempDirectory =
        Path.Combine(Path.GetTempPath(), $"botnexus-grep-limit-{Guid.NewGuid():N}");
    private readonly GrepTool _tool;

    public GrepToolLimitCeilingTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _tool = new GrepTool(_tempDirectory);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenLimitExceedsCeiling_ClampsToMaxLimit()
    {
        var prepared = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "x",
            ["limit"] = MaxLimit + 5_000
        });

        prepared["limit"].ShouldBe(MaxLimit);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenMaxResultsAliasExceedsCeiling_ClampsToMaxLimit()
    {
        var prepared = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "x",
            ["max_results"] = int.MaxValue
        });

        prepared["limit"].ShouldBe(MaxLimit);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenLimitWithinCeiling_PreservesValue()
    {
        var prepared = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "x",
            ["limit"] = 250
        });

        prepared["limit"].ShouldBe(250);
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenLimitIsZero_StillRejects()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["pattern"] = "x",
                ["limit"] = 0
            }));
    }

    [Fact]
    public async Task PrepareArgumentsAsync_WhenLimitIsNegative_StillRejects()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () =>
            await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
            {
                ["pattern"] = "x",
                ["limit"] = -1
            }));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLimitIsEnormous_DoesNotThrowAndReturnsBoundedResults()
    {
        // A single file with a handful of matches; an unbounded limit previously pre-allocated a
        // List<string> with int.MaxValue capacity and threw before reaching this point.
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory, "sample.txt"),
            "match\nmatch\nmatch");

        // Execute receives the already-prepared (clamped) limit, but pass the raw enormous value to
        // ExecuteAsync directly to exercise the defence-in-depth clamp on the allocation path.
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = "match",
            ["limit"] = int.MaxValue
        });

        result.Content.ShouldNotBeEmpty();
        result.Content[0].Value.ShouldContain("sample.txt:1: match");
    }

    [Fact]
    public async Task ExecuteAsync_WhenLimitWithinCeiling_TruncatesAtRequestedLimit()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_tempDirectory, "many.txt"),
            "match\nmatch\nmatch\nmatch");

        var prepared = await _tool.PrepareArgumentsAsync(new Dictionary<string, object?>
        {
            ["pattern"] = "match",
            ["limit"] = 2
        });

        var result = await _tool.ExecuteAsync("test-call", prepared);

        var lines = result.Content[0].Value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Count(line => line.StartsWith("many.txt:", StringComparison.Ordinal)).ShouldBe(2);
        result.Content[0].Value.ShouldContain("[warning] Results truncated at 2 matches.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
