using BotNexus.Tools;

namespace BotNexus.CodingAgent.Tests.Tools;

public sealed class GlobToolTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"botnexus-globtool-{Guid.NewGuid():N}");
    private readonly GlobTool _tool;

    public GlobToolTests()
    {
        Directory.CreateDirectory(_tempDirectory);
        _tool = new GlobTool(_tempDirectory);
    }

    [Fact]
    public async Task ExecuteAsync_MatchesExpectedFiles()
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "src"));
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "docs"));
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "src", "a.cs"), "class A {}");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "src", "b.cs"), "class B {}");
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "docs", "readme.md"), "# docs");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.cs"
        });

        result.Content.ShouldHaveSingleItem();
        result.Content[0].Value.ShouldContain(Path.Combine("src", "a.cs"));
        result.Content[0].Value.ShouldContain(Path.Combine("src", "b.cs"));
        result.Content[0].Value.ShouldNotContain(Path.Combine("docs", "readme.md"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoMatches_ReturnsNoMatchesMessage()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDirectory, "file.txt"), "text");

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.json"
        });

        result.Content[0].Value.ShouldBe("No matches.");
    }

    [Fact]
    public async Task ExecuteAsync_WhenResultsExceedLimit_ShowsTruncationNotice()
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory, "many"));
        for (var index = 0; index < 1005; index++)
        {
            File.WriteAllText(Path.Combine(_tempDirectory, "many", $"{index}.txt"), "x");
        }

        var result = await _tool.ExecuteAsync("test-call", new Dictionary<string, object?>
        {
            ["pattern"] = "**/*.txt"
        });

        result.Content[0].Value.ShouldContain("[Showing first 1000 of 1005 matches]");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
