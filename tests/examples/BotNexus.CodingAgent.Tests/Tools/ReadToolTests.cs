using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class ReadToolTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "read");
    private readonly MockFileSystem _fileSystem = new();
    private readonly ReadTool _tool;

    public ReadToolTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new ReadTool(_tempDirectory, _fileSystem);
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadingFile_ReturnsLineNumberedContent()
    {
        var filePath = Path.Combine(_tempDirectory, "sample.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "alpha\nbeta\ngamma");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "sample.txt" });

        result.Content.ShouldHaveSingleItem();
        result.Content[0].Value.ShouldBe($"alpha{Environment.NewLine}beta{Environment.NewLine}gamma");
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadingDirectory_ReturnsListing()
    {
        var nested = Path.Combine(_tempDirectory, "nested");
        _fileSystem.Directory.CreateDirectory(nested);
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(_tempDirectory, "root.txt"), "x");
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(nested, "child.txt"), "y");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });

        result.Content.ShouldHaveSingleItem();
        result.Content[0].Value.ShouldContain($"nested{Path.DirectorySeparatorChar}");
        result.Content[0].Value.ShouldContain("root.txt");
        result.Content[0].Value.ShouldContain(Path.Combine("nested", "child.txt"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenLineRangeProvided_ReturnsOnlyRequestedLines()
    {
        var filePath = Path.Combine(_tempDirectory, "range.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, "line1\nline2\nline3\nline4");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["path"] = "range.txt",
            ["offset"] = 2,
            ["limit"] = 2
        });

        result.Content[0].Value.ShouldBe($"line2{Environment.NewLine}line3{Environment.NewLine}{Environment.NewLine}[1 more lines in file. Use offset=4 to continue.]");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathDoesNotExist_ThrowsFileNotFoundException()
    {
        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "missing.txt" });

        await action.ShouldThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ExecuteAsync_WhenReadingImage_ReturnsImageContent()
    {
        var filePath = Path.Combine(_tempDirectory, "sample.png");
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR4nGNgYAAAAAMAASsJTYQAAAAASUVORK5CYII=");
        await _fileSystem.File.WriteAllBytesAsync(filePath, bytes);

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "sample.png" });

        result.Content.Count().ShouldBe(2);
        result.Content[0].Type.ShouldBe(BotNexus.Agent.Core.Types.AgentToolContentType.Text);
        result.Content[0].Value.ShouldContain("Read image file");
        result.Content[1].Type.ShouldBe(BotNexus.Agent.Core.Types.AgentToolContentType.Image);
        result.Content[1].Value.ShouldBe($"data:image/png;base64,{Convert.ToBase64String(bytes)}");
    }

    [Fact]
    public async Task ExecuteAsync_WhenByteLimitReached_ReturnsContinuationHint()
    {
        var line = new string('a', 200);
        var content = string.Join('\n', Enumerable.Range(1, 500).Select(_ => line));
        var filePath = Path.Combine(_tempDirectory, "large.txt");
        await _fileSystem.File.WriteAllTextAsync(filePath, content);

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "large.txt" });

        result.Content[0].Value.ShouldContain("51200 byte limit");
        result.Content[0].Value.ShouldContain("Use offset=");
    }

}
