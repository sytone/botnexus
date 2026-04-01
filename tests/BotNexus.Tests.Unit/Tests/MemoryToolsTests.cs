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
    public async Task MemoryGetTool_DefaultsToMemoryFile()
    {
        await _store.WriteAsync(_agentName, "MEMORY", "line one");
        var tool = new MemoryGetTool(_store, _agentName);

        var result = await tool.ExecuteAsync(new Dictionary<string, object?>());

        result.Should().Contain("# MEMORY.md");
        result.Should().Contain("line one");
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

    public void Dispose()
    {
        if (Directory.Exists(_legacyBasePath))
            Directory.Delete(_legacyBasePath, recursive: true);

        if (Directory.Exists(_agentWorkspacePath))
            Directory.Delete(_agentWorkspacePath, recursive: true);
    }
}
