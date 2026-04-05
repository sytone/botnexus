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
            ["old_str"] = "target",
            ["new_str"] = "updated"
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
            ["old_str"] = "missing",
            ["new_str"] = "replacement"
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
            ["old_str"] = "repeat",
            ["new_str"] = "once"
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
            ["old_str"] = "target",
            ["new_str"] = "planet"
        });

        result.Content[0].Value.Should().Contain("Applied replacement in 'context.txt'.");
        result.Content[0].Value.Should().Contain("hello planet world");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
