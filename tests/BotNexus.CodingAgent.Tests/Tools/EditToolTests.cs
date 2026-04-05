using BotNexus.CodingAgent.Tools;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class EditToolTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-edittool-{Guid.NewGuid():N}");
    private readonly EditTool _tool;

    public EditToolTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _tool = new EditTool(_tempDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_ReplacesSingleMatch()
    {
        var filePath = Path.Combine(_tempDirectory, "edit.txt");
        await File.WriteAllTextAsync(filePath, "before target after");

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

        (await File.ReadAllTextAsync(filePath)).Should().Be("before updated after");
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoMatches_Throws()
    {
        var filePath = Path.Combine(_tempDirectory, "no-match.txt");
        await File.WriteAllTextAsync(filePath, "content");

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
        await File.WriteAllTextAsync(filePath, "repeat repeat");

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
        await File.WriteAllTextAsync(filePath, "hello target world");

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
        await File.WriteAllTextAsync(filePath, "alpha beta gamma delta");

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

        (await File.ReadAllTextAsync(filePath)).Should().Be("alpha BETA gamma DELTA");
    }

    [Fact]
    public async Task ExecuteAsync_WhenEditProducesNoChange_Throws()
    {
        var filePath = Path.Combine(_tempDirectory, "no-change.txt");
        await File.WriteAllTextAsync(filePath, "same");

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
        await File.WriteAllTextAsync(filePath, "Console.WriteLine(“hello”);   \n");

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

        (await File.ReadAllTextAsync(filePath)).Should().Contain("Console.WriteLine(\"updated\");");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
