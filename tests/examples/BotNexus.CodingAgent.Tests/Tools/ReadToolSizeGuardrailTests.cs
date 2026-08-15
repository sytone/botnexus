using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Covers the #2689 read-path guardrails: an oversized result must name <c>offset</c>/<c>limit</c>,
/// and an unchanged re-read must take the cheap path while a CHANGED file must always return fresh
/// content. The last case is the correctness constraint the issue calls non-negotiable.
/// </summary>
public sealed class ReadToolSizeGuardrailTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "read-guardrails");
    private readonly MockFileSystem _fileSystem = new();

    public ReadToolSizeGuardrailTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
    }

    private ReadTool CreateTool(ReadToolOptions? options = null)
        => new(_tempDirectory, validator: null, _fileSystem, options);

    private async Task<string> WriteFileAsync(string name, string content)
    {
        var path = Path.Combine(_tempDirectory, name);
        await _fileSystem.File.WriteAllTextAsync(path, content);
        return name;
    }

    private static string BuildLines(int count, string prefix)
        => string.Join('\n', Enumerable.Range(1, count).Select(i => $"{prefix}{i}-{new string('x', 100)}"));

    // AC4 clause 1: a read UNDER the threshold is byte-for-byte unchanged by this feature.
    [Fact]
    public async Task ExecuteAsync_WhenResultUnderThreshold_DoesNotCarrySizeIndicator()
    {
        await WriteFileAsync("small.txt", "alpha\nbeta\ngamma");
        var tool = CreateTool();

        var result = await tool.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "small.txt" });

        result.Content[0].Value.ShouldBe($"alpha{Environment.NewLine}beta{Environment.NewLine}gamma");
        result.Content[0].Value.ShouldNotContain("Large read");
    }

    // AC1 + AC4 clause 2 + AC5 named non-vacuity test: disabling the threshold check reddens THIS test.
    [Fact]
    public async Task ExecuteAsync_WhenResultOverThreshold_CarriesSizeIndicatorNamingOffsetAndLimit()
    {
        await WriteFileAsync("big.txt", BuildLines(300, "line"));
        var tool = CreateTool(new ReadToolOptions { LargeReadThresholdBytes = 5 * 1024 });

        var result = await tool.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "big.txt" });

        var value = result.Content[0].Value;
        value.ShouldContain("Large read");
        value.ShouldContain("big.txt");
        value.ShouldContain("5120-byte threshold");
        value.ShouldContain("offset");
        value.ShouldContain("limit");
        // The indicator is additive: the content itself is still fully present.
        value.ShouldContain("line1-");
        value.ShouldContain("line300-");
    }

    // AC2 sad path: a zero/negative threshold disables the indicator entirely.
    [Fact]
    public async Task ExecuteAsync_WhenThresholdDisabled_OmitsSizeIndicatorOnLargeResult()
    {
        await WriteFileAsync("big-disabled.txt", BuildLines(300, "line"));
        var tool = CreateTool(new ReadToolOptions { LargeReadThresholdBytes = 0 });

        var result = await tool.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "big-disabled.txt" });

        result.Content[0].Value.ShouldNotContain("Large read");
    }

    // AC2: the threshold is genuinely configurable - the SAME content is quiet at a high threshold.
    [Fact]
    public async Task ExecuteAsync_WhenThresholdRaisedAboveResultSize_OmitsSizeIndicator()
    {
        await WriteFileAsync("big-raised.txt", BuildLines(300, "line"));
        var tool = CreateTool(new ReadToolOptions { LargeReadThresholdBytes = 1024 * 1024 });

        var result = await tool.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "big-raised.txt" });

        result.Content[0].Value.ShouldNotContain("Large read");
    }

    // AC3 + AC4 clause 3: an unchanged re-read in the same session takes the cheap path.
    [Fact]
    public async Task ExecuteAsync_WhenUnchangedFileIsRereadInSameSession_ReturnsShortUnchangedMarker()
    {
        await WriteFileAsync("stable.txt", BuildLines(200, "stable"));
        var tool = CreateTool();

        var first = await tool.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "stable.txt" });
        var second = await tool.ExecuteAsync("c2", new Dictionary<string, object?> { ["path"] = "stable.txt" });

        first.Content[0].Value.ShouldContain("stable1-");
        second.Content[0].Value.ShouldContain("Unchanged since your earlier read");
        second.Content[0].Value.ShouldNotContain("stable1-");
        second.Content[0].Value.Length.ShouldBeLessThan(first.Content[0].Value.Length);
        // The concurrency token still travels with the cheap path so a later edit can still use it.
        ((ReadResultDetails)second.Details!).ConcurrencyToken
            .ShouldBe(((ReadResultDetails)first.Details!).ConcurrencyToken);
    }

    // AC4 clause 4 - THE CORRECTNESS CASE THAT MUST NOT REGRESS.
    [Fact]
    public async Task ExecuteAsync_WhenFileChangedBetweenReads_ReturnsFreshContentNotUnchangedMarker()
    {
        var path = Path.Combine(_tempDirectory, "mutating.txt");
        await _fileSystem.File.WriteAllTextAsync(path, "original-alpha\noriginal-beta");
        var tool = CreateTool();

        var first = await tool.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "mutating.txt" });
        first.Content[0].Value.ShouldContain("original-alpha");

        await _fileSystem.File.WriteAllTextAsync(path, "REWRITTEN-alpha\nREWRITTEN-beta");
        var second = await tool.ExecuteAsync("c2", new Dictionary<string, object?> { ["path"] = "mutating.txt" });

        second.Content[0].Value.ShouldNotContain("Unchanged since your earlier read");
        second.Content[0].Value.ShouldContain("REWRITTEN-alpha");
        second.Content[0].Value.ShouldContain("REWRITTEN-beta");
        second.Content[0].Value.ShouldNotContain("original-alpha");
        ((ReadResultDetails)second.Details!).ConcurrencyToken
            .ShouldNotBe(((ReadResultDetails)first.Details!).ConcurrencyToken);
    }

    // AC4 clause 4 (reverted content): a file that changes and then changes BACK is still served
    // from disk, and matching content is legitimately elidable because it is genuinely identical.
    [Fact]
    public async Task ExecuteAsync_WhenFileChangedThenRestored_ThirdReadStillMatchesDiskContent()
    {
        var path = Path.Combine(_tempDirectory, "roundtrip.txt");
        await _fileSystem.File.WriteAllTextAsync(path, "v1-content");
        var tool = CreateTool();

        await tool.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "roundtrip.txt" });
        await _fileSystem.File.WriteAllTextAsync(path, "v2-content");
        var second = await tool.ExecuteAsync("c2", new Dictionary<string, object?> { ["path"] = "roundtrip.txt" });
        second.Content[0].Value.ShouldContain("v2-content");

        await _fileSystem.File.WriteAllTextAsync(path, "v3-content");
        var third = await tool.ExecuteAsync("c3", new Dictionary<string, object?> { ["path"] = "roundtrip.txt" });

        third.Content[0].Value.ShouldContain("v3-content");
        third.Content[0].Value.ShouldNotContain("Unchanged since your earlier read");
    }

    // AC3 sad path: elision is switchable off, and then a re-read pays full price again.
    [Fact]
    public async Task ExecuteAsync_WhenElisionDisabled_UnchangedRereadReturnsFullContent()
    {
        await WriteFileAsync("noelide.txt", "alpha\nbeta");
        var tool = CreateTool(new ReadToolOptions { ElideUnchangedRereads = false });

        await tool.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "noelide.txt" });
        var second = await tool.ExecuteAsync("c2", new Dictionary<string, object?> { ["path"] = "noelide.txt" });

        second.Content[0].Value.ShouldNotContain("Unchanged since your earlier read");
        second.Content[0].Value.ShouldBe($"alpha{Environment.NewLine}beta");
    }

    // A different slice of the same file is a different read - it must not be elided.
    [Fact]
    public async Task ExecuteAsync_WhenDifferentSliceRequested_DoesNotTakeTheCheapPath()
    {
        await WriteFileAsync("sliced.txt", "one\ntwo\nthree\nfour");
        var tool = CreateTool();

        await tool.ExecuteAsync("c1", new Dictionary<string, object?>
        {
            ["path"] = "sliced.txt",
            ["offset"] = 1,
            ["limit"] = 2
        });

        var second = await tool.ExecuteAsync("c2", new Dictionary<string, object?>
        {
            ["path"] = "sliced.txt",
            ["offset"] = 3,
            ["limit"] = 2
        });

        second.Content[0].Value.ShouldNotContain("Unchanged since your earlier read");
        second.Content[0].Value.ShouldContain("three");
        second.Content[0].Value.ShouldContain("four");
    }

    // The cache is per tool instance, i.e. per session. A different session pays full price.
    [Fact]
    public async Task ExecuteAsync_WhenDifferentToolInstanceReadsSameFile_DoesNotTakeTheCheapPath()
    {
        await WriteFileAsync("crosssession.txt", "alpha\nbeta");

        var sessionOne = CreateTool();
        await sessionOne.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "crosssession.txt" });

        var sessionTwo = CreateTool();
        var result = await sessionTwo.ExecuteAsync("c1", new Dictionary<string, object?> { ["path"] = "crosssession.txt" });

        result.Content[0].Value.ShouldNotContain("Unchanged since your earlier read");
        result.Content[0].Value.ShouldBe($"alpha{Environment.NewLine}beta");
    }
}
