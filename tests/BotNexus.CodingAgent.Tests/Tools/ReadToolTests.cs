using BotNexus.CodingAgent.Tools;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class ReadToolTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-readtool-{Guid.NewGuid():N}");
    private readonly ReadTool _tool;

    public ReadToolTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _tool = new ReadTool(_tempDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadingFile_ReturnsLineNumberedContent()
    {
        var filePath = Path.Combine(_tempDirectory, "sample.txt");
        await File.WriteAllTextAsync(filePath, "alpha\nbeta\ngamma");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "sample.txt" });

        result.Content.Should().ContainSingle();
        result.Content[0].Value.Should().Be($"1 | alpha{Environment.NewLine}2 | beta{Environment.NewLine}3 | gamma");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadingDirectory_ReturnsListing()
    {
        var nested = Path.Combine(_tempDirectory, "nested");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "root.txt"), "x");
        await File.WriteAllTextAsync(Path.Combine(nested, "child.txt"), "y");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });

        result.Content.Should().ContainSingle();
        result.Content[0].Value.Should().Contain($"nested{Path.DirectorySeparatorChar}");
        result.Content[0].Value.Should().Contain("root.txt");
        result.Content[0].Value.Should().Contain(Path.Combine("nested", "child.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLineRangeProvided_ReturnsOnlyRequestedLines()
    {
        var filePath = Path.Combine(_tempDirectory, "range.txt");
        await File.WriteAllTextAsync(filePath, "line1\nline2\nline3\nline4");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "range.txt",
            ["start_line"] = 2,
            ["end_line"] = 3
        });

        result.Content[0].Value.Should().Be($"2 | line2{Environment.NewLine}3 | line3");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathDoesNotExist_ThrowsFileNotFoundException()
    {
        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "missing.txt" });

        await action.Should().ThrowAsync<FileNotFoundException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
