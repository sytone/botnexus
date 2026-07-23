using System.Text.Json;
using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class EditToolTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "edit");
    private readonly MockFileSystem _fileSystem = new();
    private readonly EditTool _tool;

    public EditToolTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new EditTool(_tempDirectory, _fileSystem);
    }

    [Fact]
    public async Task ExecuteAsync_ReplacesSingleMatch()
    {
        var filePath = Path.Combine(_tempDirectory, "edit.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "before target after");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "edit.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "target",
                    ["newText"] = "updated"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("before updated after");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoMatches_Throws()
    {
        var filePath = Path.Combine(_tempDirectory, "no-match.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "content");

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "no-match.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "missing",
                    ["newText"] = "replacement"
                }
            }
        });

        (await action.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldContain("found 0");
    }

    [Fact]
    public async Task ExecuteAsync_WhenMultipleMatches_Throws()
    {
        var filePath = Path.Combine(_tempDirectory, "multi-match.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "repeat repeat");

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "multi-match.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "repeat",
                    ["newText"] = "once"
                }
            }
        });

        (await action.ShouldThrowAsync<InvalidOperationException>())
            .Message.ShouldContain("found 2");
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsReplacementContext()
    {
        var filePath = Path.Combine(_tempDirectory, "context.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "hello target world");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "context.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "target",
                    ["newText"] = "planet"
                }
            }
        });

        result.Content[0].Value.ShouldContain("Successfully replaced 1 block(s) in 'context.txt'.");
        result.Content[0].Value.ShouldContain("hello planet world");
    }

    [Fact]
    public async Task ExecuteAsync_AppliesMultipleEdits()
    {
        var filePath = Path.Combine(_tempDirectory, "multi-edit.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "alpha beta gamma delta");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "multi-edit.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "beta",
                    ["newText"] = "BETA"
                },
                new Dictionary<string, object?>
                {
                    ["oldText"] = "delta",
                    ["newText"] = "DELTA"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("alpha BETA gamma DELTA");
    }

    [Fact]
    public async Task ExecuteAsync_WhenSingleEditIsIdentical_ReturnsIdempotentNoOp()
    {
        // Issue #2100: a replacement whose newText already matches the target is a
        // successful idempotent no-op, not a hard failure.
        var filePath = Path.Combine(_tempDirectory, "no-change.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "same");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "no-change.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "same",
                    ["newText"] = "same"
                }
            }
        });

        result.Content[0].Value.ShouldContain("No changes needed");
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("same");
        result.Details.ShouldBeOfType<EditResultDetails>().Changed.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenAllEditsAreIdentical_ReturnsIdempotentNoOp()
    {
        var filePath = Path.Combine(_tempDirectory, "all-noop.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "alpha beta gamma");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "all-noop.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "alpha",
                    ["newText"] = "alpha"
                },
                new Dictionary<string, object?>
                {
                    ["oldText"] = "gamma",
                    ["newText"] = "gamma"
                }
            }
        });

        result.Content[0].Value.ShouldContain("No changes needed");
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("alpha beta gamma");
        result.Details.ShouldBeOfType<EditResultDetails>().Changed.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_WhenMixedNoOpAndRealEdit_AppliesRealEditAndReportsSatisfied()
    {
        // Issue #2100: a mixed batch must not fail just because one entry is already
        // satisfied; the real edit applies and the no-op index is reported.
        var filePath = Path.Combine(_tempDirectory, "mixed.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "alpha beta gamma");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "mixed.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "alpha",
                    ["newText"] = "alpha"
                },
                new Dictionary<string, object?>
                {
                    ["oldText"] = "gamma",
                    ["newText"] = "GAMMA"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("alpha beta GAMMA");
        var details = result.Details.ShouldBeOfType<EditResultDetails>();
        details.Changed.ShouldBeTrue();
        details.AlreadySatisfiedIndices.ShouldContain(0);
        result.Content[0].Value.ShouldContain("already present");
    }

    [Fact]
    public async Task ExecuteAsync_FuzzyMatchNormalizesSmartQuotesAndTrailingWhitespace()
    {
        var filePath = Path.Combine(_tempDirectory, "smart-quotes.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "Console.WriteLine(“hello”);   \n");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "smart-quotes.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "Console.WriteLine(\"hello\");",
                    ["newText"] = "Console.WriteLine(\"updated\");"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldContain("Console.WriteLine(\"updated\");");
    }

    [Fact]
    public async Task ExecuteAsync_MatchesOldTextWhenFileStartsWithBom()
    {
        var filePath = Path.Combine(_tempDirectory, "bom.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "\uFEFFbefore target after");

        await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "bom.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "before target after",
                    ["newText"] = "before updated after"
                }
            }
        });

        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldContain("before updated after");
    }

    [Fact]
    public async Task ExecuteAsync_SingleLineEdit_ProducesCompactUnifiedDiffHunk()
    {
        var filePath = Path.Combine(_tempDirectory, "compact-diff.txt");
        var lines = Enumerable.Range(1, 20).Select(index => $"line {index}");
        await _fileSystem.File.WriteAllTextAsync(filePath, string.Join('\n', lines));

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "compact-diff.txt",
            ["edits"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["oldText"] = "line 10",
                    ["newText"] = "line 10 updated"
                }
            }
        });

        var outputLines = result.Content[0].Value
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        outputLines.Count().ShouldBeLessThanOrEqualTo(12);
        outputLines.ShouldContain(line => line.StartsWith("@@ -", StringComparison.Ordinal) && line.EndsWith(" @@", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WithPreparedArguments_DoesNotDoubleParse()
    {
        // Regression test: PrepareArgumentsAsync converts edits into EditEntry objects.
        // Before the fix, ExecuteAsync would re-parse those entries via ReadEdits, hitting
        // "Each edits entry must be an object." because EditEntry wasn't handled.
        var filePath = Path.Combine(_tempDirectory, "double-parse.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "hello world");

        using var doc = JsonDocument.Parse("""{"edits": [{"oldText": "hello", "newText": "goodbye"}]}""");
        var editsElement = doc.RootElement.GetProperty("edits").Clone();

        var rawArgs = new Dictionary<string, object?>
        {
            ["path"] = "double-parse.txt",
            ["edits"] = editsElement
        };

        var preparedArgs = await _tool.PrepareArgumentsAsync(rawArgs);
        var result = await _tool.ExecuteAsync("test-call", preparedArgs);

        result.Content[0].Value.ShouldContain("Successfully replaced");
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("goodbye world");
    }

    [Fact]
    public async Task ExecuteAsync_WithRawJsonElementArgs_StillWorks()
    {
        // Exercises the fallback path where ExecuteAsync is called directly with
        // raw JsonElement args, bypassing PrepareArgumentsAsync.
        var filePath = Path.Combine(_tempDirectory, "raw-json.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "hello world");

        using var doc = JsonDocument.Parse("""{"edits": [{"oldText": "hello", "newText": "goodbye"}]}""");
        var editsElement = doc.RootElement.GetProperty("edits").Clone();

        var rawArgs = new Dictionary<string, object?>
        {
            ["path"] = "raw-json.txt",
            ["edits"] = editsElement
        };

        var result = await _tool.ExecuteAsync("test-call", rawArgs);

        result.Content[0].Value.ShouldContain("Successfully replaced");
        (await _fileSystem.File.ReadAllTextAsync(filePath)).ShouldBe("goodbye world");
    }

}
