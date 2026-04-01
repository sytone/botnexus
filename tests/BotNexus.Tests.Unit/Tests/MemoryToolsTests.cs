using System.Text.RegularExpressions;
using BotNexus.Agent;
using BotNexus.Agent.Tools;
using BotNexus.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Unit.Tests;

[Collection("BotNexusHomeEnvVar")]
public sealed class MemoryToolsTests : IDisposable
{
    private readonly string _legacyBasePath;
    private readonly MemoryStore _store;
    private readonly string _agentName = $"bender-memory-tools-{Guid.NewGuid():N}";
    private readonly string _agentWorkspacePath;

    public MemoryToolsTests()
    {
        _legacyBasePath = Path.Combine(Path.GetTempPath(), $"botnexus-legacy-memory-tools-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_legacyBasePath);
        _store = new MemoryStore(_legacyBasePath, NullLogger<MemoryStore>.Instance);
        _agentWorkspacePath = BotNexusHome.GetAgentWorkspacePath(_agentName);
        Directory.CreateDirectory(_agentWorkspacePath);
    }

    [Fact]
    public async Task MemorySearchTool_KeywordMatch_ReturnsOnlyMatchingEntries()
    {
        await _store.WriteAsync(_agentName, "daily/2026-04-01", "no relevant content");
        await _store.WriteAsync(_agentName, "daily/2026-04-02", "project phoenix launch");

        var tool = new MemorySearchTool(_store, _agentName);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "phoenix"
        });

        result.Should().Contain("Found 1 result(s) for 'phoenix'");
        result.Should().Contain("memory/daily/2026-04-02.md");
        result.Should().NotContain("memory/daily/2026-04-01.md");
    }

    [Fact]
    public async Task MemorySearchTool_IsCaseInsensitive()
    {
        await _store.WriteAsync(_agentName, "daily/2026-04-02", "Deploy COPILOT provider");
        var tool = new MemorySearchTool(_store, _agentName);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "copilot"
        });

        result.Should().Contain("memory/daily/2026-04-02.md");
        result.Should().Contain("Deploy COPILOT provider");
    }

    [Fact]
    public async Task MemorySearchTool_ReturnsResultsByRecency_DailyFirst_MemoryLast()
    {
        await _store.WriteAsync(_agentName, "MEMORY", "alpha in memory");
        await _store.WriteAsync(_agentName, "daily/2026-04-01", "alpha old daily");
        await _store.WriteAsync(_agentName, "daily/2026-04-02", "alpha newest daily");

        var tool = new MemorySearchTool(_store, _agentName);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "alpha",
            ["max_results"] = 10
        });

        var newestIndex = result.IndexOf("memory/daily/2026-04-02.md", StringComparison.Ordinal);
        var oldIndex = result.IndexOf("memory/daily/2026-04-01.md", StringComparison.Ordinal);
        var memoryIndex = result.IndexOf("MEMORY.md", StringComparison.Ordinal);

        newestIndex.Should().BeGreaterThan(-1);
        oldIndex.Should().BeGreaterThan(newestIndex);
        memoryIndex.Should().BeGreaterThan(oldIndex);
    }

    [Fact]
    public async Task MemorySearchTool_IncludesTwoLinesOfContext()
    {
        await _store.WriteAsync(_agentName, "daily/2026-04-01", "l1\nl2\nmatch-here\nl4\nl5");
        var tool = new MemorySearchTool(_store, _agentName);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "match-here"
        });

        result.Should().Contain("   1: l1");
        result.Should().Contain("   2: l2");
        result.Should().Contain("   3: match-here");
        result.Should().Contain("   4: l4");
        result.Should().Contain("   5: l5");
    }

    [Fact]
    public async Task MemorySearchTool_NoResults_ReturnsHelpfulMessage()
    {
        await _store.WriteAsync(_agentName, "daily/2026-04-01", "alpha beta gamma");
        var tool = new MemorySearchTool(_store, _agentName);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "delta"
        });

        result.Should().Be("No matches found for 'delta' in memory files.");
    }

    [Fact]
    public async Task MemorySearchTool_RespectsMaxResults()
    {
        await _store.WriteAsync(_agentName, "daily/2026-04-02", "match-1\nmatch-2\nmatch-3");
        var tool = new MemorySearchTool(_store, _agentName);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = "match",
            ["max_results"] = 2
        });

        result.Should().Contain("Found 2 result(s) for 'match'");
        Regex.Matches(result, @"^\[\d+\]", RegexOptions.Multiline).Count.Should().Be(2);
    }

    [Fact]
    public async Task MemorySearchTool_EmptyOrNullQuery_ReturnsHelpfulError()
    {
        var tool = new MemorySearchTool(_store, _agentName);

        var nullResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = null
        });
        var emptyResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["query"] = ""
        });

        nullResult.Should().Be("Error: 'query' is required and must be a non-empty string.");
        emptyResult.Should().Be("Error: 'query' is required and must be a non-empty string.");
    }

    [Fact]
    public async Task MemorySaveTool_Daily_WritesTimestampedEntryToTodayFile()
    {
        var tool = new MemorySaveTool(_store, _agentName);
        var response = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["content"] = "remember this"
        });

        response.Should().Contain("Saved to memory/daily/");
        var todayKey = $"daily/{DateTimeOffset.Now:yyyy-MM-dd}";
        var saved = await _store.ReadAsync(_agentName, todayKey);
        saved.Should().NotBeNull();
        Regex.IsMatch(saved!, @"^\[\d{2}:\d{2}\] remember this", RegexOptions.Multiline).Should().BeTrue();
    }

    [Fact]
    public async Task MemorySaveTool_Memory_AppendsUnderNotesSection()
    {
        await _store.WriteAsync(_agentName, "MEMORY", "# Memory\n\n## Existing\n\n- prior");
        var tool = new MemorySaveTool(_store, _agentName);

        var response = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["content"] = "new memory line",
            ["target"] = "memory"
        });

        response.Should().Contain("Saved to MEMORY.md");
        var saved = await _store.ReadAsync(_agentName, "MEMORY");
        saved.Should().Contain("## Notes");
        saved.Should().Contain("- new memory line");
    }

    [Fact]
    public async Task MemorySaveTool_Memory_CreatesFileWhenMissing()
    {
        var tool = new MemorySaveTool(_store, _agentName);
        var response = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["content"] = "first note",
            ["target"] = "memory"
        });

        response.Should().Contain("Saved to MEMORY.md");
        var saved = await _store.ReadAsync(_agentName, "MEMORY");
        saved.Should().NotBeNull();
        saved.Should().Contain("## Notes");
        saved.Should().Contain("- first note");
    }

    [Fact]
    public async Task MemorySaveTool_EmptyOrNullContent_ReturnsHelpfulError()
    {
        var tool = new MemorySaveTool(_store, _agentName);

        var nullResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["content"] = null
        });
        var emptyResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["content"] = " "
        });

        nullResult.Should().Be("Error: 'content' is required and must be a non-empty string.");
        emptyResult.Should().Be("Error: 'content' is required and must be a non-empty string.");
    }

    [Fact]
    public async Task MemoryGetTool_DefaultsToMemoryFile()
    {
        await _store.WriteAsync(_agentName, "MEMORY", "line one");
        var tool = new MemoryGetTool(_store, _agentName);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());

        result.Should().Contain("# MEMORY.md");
        result.Should().Contain("line one");
    }

    [Fact]
    public async Task MemoryGetTool_ReturnsSpecificDailyFileByDate()
    {
        await _store.WriteAsync(_agentName, "daily/2026-04-01", "daily-only-content");
        var tool = new MemoryGetTool(_store, _agentName);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["file"] = "2026-04-01"
        });

        result.Should().Contain("# memory/daily/2026-04-01.md");
        result.Should().Contain("daily-only-content");
    }

    [Fact]
    public async Task MemoryGetTool_DateAndLines_ReturnsSelectedRange()
    {
        await _store.WriteAsync(_agentName, "daily/2026-04-01", "a\nb\nc\nd");
        var tool = new MemoryGetTool(_store, _agentName);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["file"] = "2026-04-01",
            ["lines"] = "2-3"
        });

        result.Should().Contain("# memory/daily/2026-04-01.md (lines 2-3)");
        result.Should().Contain("   2: b");
        result.Should().Contain("   3: c");
        result.Should().NotContain("   1: a");
    }

    [Fact]
    public async Task MemoryGetTool_MissingFile_ReturnsHelpfulMessage()
    {
        var tool = new MemoryGetTool(_store, _agentName);
        var result = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["file"] = "2026-04-01"
        });

        result.Should().Contain("No memory file found");
    }

    [Fact]
    public async Task MemoryGetTool_EmptyOrNullInput_HandledGracefully()
    {
        await _store.WriteAsync(_agentName, "MEMORY", "line one");
        var tool = new MemoryGetTool(_store, _agentName);

        var nullFileResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["file"] = null
        });
        var invalidLinesResult = await tool.ExecuteAsync(new Dictionary<string, object?>
        {
            ["file"] = "memory",
            ["lines"] = "bad-range"
        });

        nullFileResult.Should().Contain("# MEMORY.md");
        invalidLinesResult.Should().Be("Error: Invalid 'lines' format. Use '<start>-<end>' (e.g., '10-20').");
    }

    [Fact]
    public void MemoryTools_Definitions_AreCorrect()
    {
        var search = new MemorySearchTool(_store, _agentName).Definition;
        var save = new MemorySaveTool(_store, _agentName).Definition;
        var get = new MemoryGetTool(_store, _agentName).Definition;

        search.Name.Should().Be("memory_search");
        search.Description.Should().Contain("Search");
        search.Parameters.Should().ContainKey("query");
        search.Parameters["query"].Type.Should().Be("string");
        search.Parameters["query"].Required.Should().BeTrue();
        search.Parameters.Should().ContainKey("max_results");
        search.Parameters["max_results"].Type.Should().Be("integer");
        search.Parameters["max_results"].Required.Should().BeFalse();

        save.Name.Should().Be("memory_save");
        save.Description.Should().Contain("Save");
        save.Parameters.Should().ContainKey("content");
        save.Parameters["content"].Type.Should().Be("string");
        save.Parameters["content"].Required.Should().BeTrue();
        save.Parameters.Should().ContainKey("target");
        save.Parameters["target"].EnumValues.Should().Equal("memory", "daily");

        get.Name.Should().Be("memory_get");
        get.Description.Should().Contain("Read");
        get.Parameters.Should().ContainKey("file");
        get.Parameters["file"].Type.Should().Be("string");
        get.Parameters["file"].Required.Should().BeFalse();
        get.Parameters.Should().ContainKey("lines");
        get.Parameters["lines"].Type.Should().Be("string");
        get.Parameters["lines"].Required.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_legacyBasePath))
            Directory.Delete(_legacyBasePath, recursive: true);

        if (Directory.Exists(_agentWorkspacePath))
            Directory.Delete(_agentWorkspacePath, recursive: true);
    }
}
