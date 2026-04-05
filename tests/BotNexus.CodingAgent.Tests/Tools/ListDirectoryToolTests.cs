using BotNexus.CodingAgent.Tools;
using FluentAssertions;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class ListDirectoryToolTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-listdirectorytool-{Guid.NewGuid():N}");
    private readonly ListDirectoryTool _tool;

    public ListDirectoryToolTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _tool = new ListDirectoryTool(_tempDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_ListsFilesAndSubdirectories()
    {
        var nestedDirectory = Path.Combine(_tempDirectory, "src");
        Directory.CreateDirectory(nestedDirectory);
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "root.txt"), "root");
        await File.WriteAllTextAsync(Path.Combine(nestedDirectory, "child.txt"), "child");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });

        result.Content[0].Value.Should().Contain("src/");
        result.Content[0].Value.Should().Contain("root.txt");
        result.Content[0].Value.Should().NotContain("child.txt");
    }

    [Fact]
    public async Task ExecuteAsync_DepthLimitsTraversal()
    {
        var levelOne = Path.Combine(_tempDirectory, "level1");
        var levelTwo = Path.Combine(levelOne, "level2");
        Directory.CreateDirectory(levelTwo);
        await File.WriteAllTextAsync(Path.Combine(levelTwo, "file.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "another.txt"), "x");

        var depthOne = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });
        var depthTwo = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = ".", ["limit"] = 1 });

        depthOne.Content[0].Value.Should().Contain("level1/");
        depthOne.Content[0].Value.Should().NotContain("level2/");
        depthOne.Content[0].Value.Should().NotContain("file.txt");
        depthTwo.Content[0].Value.Should().Contain("entries limit reached");
    }

    [Fact]
    public async Task ExecuteAsync_ShowHiddenControlsHiddenEntries()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "visible.txt"), "visible");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, ".hidden.txt"), "hidden");

        var listing = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });

        listing.Content[0].Value.Should().Contain("visible.txt");
        listing.Content[0].Value.Should().Contain(".hidden.txt");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathMissing_ReturnsErrorResult()
    {
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "missing" });
        result.Content[0].Value.Should().Contain("does not exist or is not a directory");
    }

    [Fact]
    public async Task ExecuteAsync_WhenDirectoryEmpty_ReturnsEmptyMessage()
    {
        var empty = Path.Combine(_tempDirectory, "empty");
        Directory.CreateDirectory(empty);

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "empty" });
        result.Content[0].Value.Should().Be("(empty directory)");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathEscapesWorkingDirectory_Throws()
    {
        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "..\\outside" });
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
