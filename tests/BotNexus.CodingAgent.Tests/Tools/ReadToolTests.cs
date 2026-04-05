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
        result.Content[0].Value.Should().Be($"alpha{Environment.NewLine}beta{Environment.NewLine}gamma");
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
            ["offset"] = 2,
            ["limit"] = 2
        });

        result.Content[0].Value.Should().Be($"line2{Environment.NewLine}line3{Environment.NewLine}{Environment.NewLine}[1 more lines in file. Use offset=4 to continue.]");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathDoesNotExist_ThrowsFileNotFoundException()
    {
        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "missing.txt" });

        await action.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadingImage_ReturnsImageContent()
    {
        var filePath = Path.Combine(_tempDirectory, "sample.png");
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(filePath, bytes);

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "sample.png" });

        result.Content.Should().HaveCount(2);
        result.Content[0].Type.Should().Be(BotNexus.AgentCore.Types.AgentToolContentType.Text);
        result.Content[0].Value.Should().Contain("Read image file");
        result.Content[1].Type.Should().Be(BotNexus.AgentCore.Types.AgentToolContentType.Image);
        result.Content[1].Value.Should().Be($"data:image/png;base64,{Convert.ToBase64String(bytes)}");
    }

    [Fact]
    public async Task ExecuteAsync_WhenByteLimitReached_ReturnsContinuationHint()
    {
        var line = new string('a', 200);
        var content = string.Join('\n', Enumerable.Range(1, 500).Select(_ => line));
        var filePath = Path.Combine(_tempDirectory, "large.txt");
        await File.WriteAllTextAsync(filePath, content);

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "large.txt" });

        result.Content[0].Value.Should().Contain("50000 byte limit");
        result.Content[0].Value.Should().Contain("Use offset=");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
