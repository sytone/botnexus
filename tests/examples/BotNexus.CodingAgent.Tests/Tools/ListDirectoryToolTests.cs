using BotNexus.Tools;
using System.IO.Abstractions.TestingHelpers;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class ListDirectoryToolTests
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "tools", "ls");
    private readonly MockFileSystem _fileSystem = new();
    private readonly ListDirectoryTool _tool;

    public ListDirectoryToolTests()
    {
        _fileSystem.Directory.CreateDirectory(_tempDirectory);
        _tool = new ListDirectoryTool(_tempDirectory, _fileSystem);
    }

    [Fact]
    public async Task ExecuteAsync_ListsTwoLevelsDeep()
    {
        var nestedDirectory = Path.Combine(_tempDirectory, "src");
        var grandchildDirectory = Path.Combine(nestedDirectory, "agent");
        var tooDeepDirectory = Path.Combine(grandchildDirectory, "deep");
        _fileSystem.Directory.CreateDirectory(tooDeepDirectory);
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(_tempDirectory, "root.txt"), "root");
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(grandchildDirectory, "child.txt"), "child");
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(tooDeepDirectory, "too-deep.txt"), "nope");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });

        result.Content[0].Value.ShouldContain("src/");
        result.Content[0].Value.ShouldContain("src/agent/");
        result.Content[0].Value.ShouldContain("src/agent/child.txt");
        result.Content[0].Value.ShouldNotContain("src/agent/deep/too-deep.txt");
    }

    [Fact]
    public async Task ExecuteAsync_DirectoriesHaveTrailingSlash()
    {
        _fileSystem.Directory.CreateDirectory(Path.Combine(_tempDirectory, "alpha"));
        _fileSystem.Directory.CreateDirectory(Path.Combine(_tempDirectory, "alpha", "beta"));
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(_tempDirectory, "alpha", "beta", "child.txt"), "child");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });

        result.Content[0].Value.ShouldContain("alpha/");
        result.Content[0].Value.ShouldContain("alpha/beta/");
        result.Content[0].Value.ShouldNotContain("alpha/beta/child.txt/");
    }

    [Fact]
    public async Task ExecuteAsync_ShowHiddenControlsHiddenEntries()
    {
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(_tempDirectory, "visible.txt"), "visible");
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(_tempDirectory, ".hidden.txt"), "hidden");

        var listing = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });

        listing.Content[0].Value.ShouldContain("visible.txt");
        listing.Content[0].Value.ShouldContain(".hidden.txt");
    }

    [Fact]
    public async Task ExecuteAsync_OutputFormatMatchesSpec()
    {
        _fileSystem.Directory.CreateDirectory(Path.Combine(_tempDirectory, "src"));
        _fileSystem.Directory.CreateDirectory(Path.Combine(_tempDirectory, "src", "agent"));
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(_tempDirectory, "README.md"), "readme");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });

        var lines = result.Content[0].Value
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Take(3)
            .ToArray();
        lines.ShouldContain("README.md");
        lines.ShouldContain("src/");
        lines.ShouldContain("src/agent/");
    }

    [Fact]
    public async Task ExecuteAsync_CapsOutputAtMaxEntries()
    {
        for (var i = 0; i < 550; i++)
        {
            _fileSystem.File.WriteAllText(Path.Combine(_tempDirectory, $"file-{i:D3}.txt"), "x");
        }

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "." });
        var lines = result.Content[0].Value.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        lines.ShouldContain(line => line.Contains("500 entries limit reached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathMissing_ReturnsErrorResult()
    {
        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "missing" });
        result.Content[0].Value.ShouldContain("does not exist or is not a directory");
    }

    [Fact]
    public async Task ExecuteAsync_WhenDirectoryEmpty_ReturnsEmptyMessage()
    {
        var empty = Path.Combine(_tempDirectory, "empty");
        _fileSystem.Directory.CreateDirectory(empty);

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "empty" });
        result.Content[0].Value.ShouldBe("(empty directory)");
    }

    [Fact]
    public async Task ExecuteAsync_WhenPathEscapesWorkingDirectory_Throws()
    {
        var action = () => _tool.ExecuteAsync("test-call", new Dictionary<string, object?> { ["path"] = "../outside" });
        await action.ShouldThrowAsync<InvalidOperationException>();
    }

}
