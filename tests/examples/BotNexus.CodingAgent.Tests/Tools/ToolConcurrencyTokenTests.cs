using BotNexus.Tools;
using BotNexus.Tools.Utils;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

/// <summary>
/// Tests for the optimistic-concurrency token surfaced by <c>read</c> and accepted by <c>edit</c>
/// (issue #2101). The token lets an edit detect that the file changed since it was read and return
/// a deterministic stale-content outcome instead of a blind "found 0" fuzzy-match failure.
/// </summary>
public sealed class ToolConcurrencyTokenTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "concurrency");
    private readonly MockFileSystem _fileSystem = new();
    private readonly ReadTool _readTool;
    private readonly EditTool _editTool;

    public ToolConcurrencyTokenTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _readTool = new ReadTool(_tempDirectory, _fileSystem);
        _editTool = new EditTool(_tempDirectory, _fileSystem);
    }

    [Fact]
    public async Task ReadTool_ExposesConcurrencyTokenInDetails()
    {
        var filePath = Path.Combine(_tempDirectory, "token.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "alpha\nbeta\ngamma");

        var result = await _readTool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "token.txt" });

        var details = result.Details.ShouldBeOfType<ReadResultDetails>();
        details.ConcurrencyToken.ShouldNotBeNullOrWhiteSpace();
        details.ConcurrencyToken.ShouldBe(ContentToken.Compute("alpha\nbeta\ngamma"));
    }

    [Fact]
    public async Task EditTool_WithMatchingToken_AppliesEdit()
    {
        var filePath = Path.Combine(_tempDirectory, "match.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "before target after");

        var read = await _readTool.ExecuteAsync("read", new Dictionary<string, object?> { ["path"] = "match.txt" });
        var token = read.Details.ShouldBeOfType<ReadResultDetails>().ConcurrencyToken;

        var result = await _editTool.ExecuteAsync("edit", new Dictionary<string, object?>
        {
            ["path"] = "match.txt",
            ["expectedHash"] = token,
            ["edits"] = new object[]
            {
                new Dictionary<string, object?> { ["oldText"] = "target", ["newText"] = "updated" }
            }
        });

        result.Details.ShouldBeOfType<EditResultDetails>().Changed.ShouldBeTrue();
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("before updated after");
    }

    [Fact]
    public async Task EditTool_WithStaleToken_ReturnsStructuredStaleResultWithoutApplying()
    {
        var filePath = Path.Combine(_tempDirectory, "stale.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "before target after");

        var read = await _readTool.ExecuteAsync("read", new Dictionary<string, object?> { ["path"] = "stale.txt" });
        var token = read.Details.ShouldBeOfType<ReadResultDetails>().ConcurrencyToken;

        // Simulate another writer changing the file after the read but before the edit.
        await _fileSystem.File.WriteAllTextAsync(filePath, "before target after CHANGED");

        var result = await _editTool.ExecuteAsync("edit", new Dictionary<string, object?>
        {
            ["path"] = "stale.txt",
            ["expectedHash"] = token,
            ["edits"] = new object[]
            {
                new Dictionary<string, object?> { ["oldText"] = "target", ["newText"] = "updated" }
            }
        });

        var details = result.Details.ShouldBeOfType<EditStaleContentDetails>();
        details.ExpectedHash.ShouldBe(token);
        details.ActualHash.ShouldNotBe(token);
        result.Content[0].Value.ShouldContain("File changed since read");
        // File must be left untouched - no silent wrong apply.
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("before target after CHANGED");
    }

    [Fact]
    public async Task EditTool_WithoutToken_BehavesExactlyAsBefore()
    {
        var filePath = Path.Combine(_tempDirectory, "notoken.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "before target after");

        var result = await _editTool.ExecuteAsync("edit", new Dictionary<string, object?>
        {
            ["path"] = "notoken.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?> { ["oldText"] = "target", ["newText"] = "updated" }
            }
        });

        result.Details.ShouldBeOfType<EditResultDetails>().Changed.ShouldBeTrue();
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("before updated after");
    }
}
