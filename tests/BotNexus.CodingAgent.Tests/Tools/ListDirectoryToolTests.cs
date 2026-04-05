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

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = ".", ["depth"] = 2 });

        result.Content[0].Value.Should().Contain($".{Path.DirectorySeparatorChar}");
        result.Content[0].Value.Should().Contain($"src{Path.DirectorySeparatorChar} [dir]");
        result.Content[0].Value.Should().Contain("root.txt [file, 4 bytes]");
        result.Content[0].Value.Should().Contain("child.txt [file, 5 bytes]");
    }

    [Fact]
    public async Task ExecuteAsync_DepthLimitsTraversal()
    {
        var levelOne = Path.Combine(_tempDirectory, "level1");
        var levelTwo = Path.Combine(levelOne, "level2");
        Directory.CreateDirectory(levelTwo);
        await File.WriteAllTextAsync(Path.Combine(levelTwo, "file.txt"), "content");

        var depthOne = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = ".", ["depth"] = 1 });
        var depthTwo = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = ".", ["depth"] = 2 });

        depthOne.Content[0].Value.Should().Contain($"level1{Path.DirectorySeparatorChar} [dir]");
        depthOne.Content[0].Value.Should().NotContain($"level2{Path.DirectorySeparatorChar} [dir]");
        depthTwo.Content[0].Value.Should().Contain($"level2{Path.DirectorySeparatorChar} [dir]");
        depthTwo.Content[0].Value.Should().NotContain("file.txt [file, 7 bytes]");
    }

    [Fact]
    public async Task ExecuteAsync_ShowHiddenControlsHiddenEntries()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "visible.txt"), "visible");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, ".hidden.txt"), "hidden");

        var hiddenExcluded = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = ".", ["showHidden"] = false });
        var hiddenIncluded = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = ".", ["showHidden"] = true });

        hiddenExcluded.Content[0].Value.Should().Contain("visible.txt");
        hiddenExcluded.Content[0].Value.Should().NotContain(".hidden.txt");
        hiddenIncluded.Content[0].Value.Should().Contain(".hidden.txt");
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
        result.Content[0].Value.Should().Contain("is empty.");
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
