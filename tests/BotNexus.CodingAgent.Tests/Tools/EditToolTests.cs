using BotNexus.Tools;
using FluentAssertions;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class EditToolTests
{
    private readonly string _tempDirectory = @"C:\tools\edit";
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

        (await _fileSystem.File.ReadAllTextAsync(filePath)).Should().Be("before updated after");
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

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*found 0*");
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

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*found 2*");
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

        result.Content[0].Value.Should().Contain("Successfully replaced 1 block(s) in 'context.txt'.");
        result.Content[0].Value.Should().Contain("hello planet world");
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

        (await _fileSystem.File.ReadAllTextAsync(filePath)).Should().Be("alpha BETA gamma DELTA");
    }

    [Fact]
    public async Task ExecuteAsync_WhenEditProducesNoChange_Throws()
    {
        var filePath = Path.Combine(_tempDirectory, "no-change.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "same");

        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
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

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Edit produced no change*");
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

        (await _fileSystem.File.ReadAllTextAsync(filePath)).Should().Contain("Console.WriteLine(\"updated\");");
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

        (await _fileSystem.File.ReadAllTextAsync(filePath)).Should().Contain("before updated after");
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
        outputLines.Should().HaveCountLessThanOrEqualTo(12);
        outputLines.Should().Contain(line => line.StartsWith("@@ -", StringComparison.Ordinal) && line.EndsWith(" @@", StringComparison.Ordinal));
    }

}
