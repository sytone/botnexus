using BotNexus.Agent;
using BotNexus.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class MemoryStoreTests : IDisposable
{
    private const string HomeOverrideEnvVar = "BOTNEXUS_HOME";
    private readonly string? _originalHomeOverride;
    private readonly string _tempHomePath;
    private readonly string _legacyBasePath;
    private readonly MemoryStore _store;

    public MemoryStoreTests()
    {
        _originalHomeOverride = Environment.GetEnvironmentVariable(HomeOverrideEnvVar);
        _tempHomePath = Path.Combine(Path.GetTempPath(), $"botnexus-home-mem-test-{Guid.NewGuid():N}");
        _legacyBasePath = Path.Combine(Path.GetTempPath(), $"botnexus-legacy-mem-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempHomePath);
        Directory.CreateDirectory(_legacyBasePath);
        Environment.SetEnvironmentVariable(HomeOverrideEnvVar, _tempHomePath);
        _store = new MemoryStore(_legacyBasePath, NullLogger<MemoryStore>.Instance);
    }

    [Fact]
    public async Task ReadAsync_NonExistentKey_ReturnsNull()
    {
        var result = await _store.ReadAsync("agent1", "nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task WriteAndRead_RoundTrips_Content()
    {
        await _store.WriteAsync("agent1", "notes", "My important notes");
        var result = await _store.ReadAsync("agent1", "notes");
        result.Should().Be("My important notes");

        var expectedPath = Path.Combine(BotNexusHome.GetAgentWorkspacePath("agent1"), "memory", "notes.md");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_Overwrites_ExistingContent()
    {
        await _store.WriteAsync("agent1", "key1", "original");
        await _store.WriteAsync("agent1", "key1", "updated");
        var result = await _store.ReadAsync("agent1", "key1");
        result.Should().Be("updated");
    }

    [Fact]
    public async Task AppendAsync_AddsToExisting()
    {
        await _store.WriteAsync("agent1", "log", "line1\n");
        await _store.AppendAsync("agent1", "log", "line2\n");
        var result = await _store.ReadAsync("agent1", "log");
        result.Should().Contain("line1");
        result.Should().Contain("line2");
    }

    [Fact]
    public async Task DeleteAsync_RemovesFile()
    {
        await _store.WriteAsync("agent1", "toDelete", "content");
        await _store.DeleteAsync("agent1", "toDelete");
        var result = await _store.ReadAsync("agent1", "toDelete");
        result.Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistentKey_DoesNotThrow()
    {
        var act = async () => await _store.DeleteAsync("agent1", "nonexistent");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ListKeysAsync_EmptyAgent_ReturnsEmpty()
    {
        var keys = await _store.ListKeysAsync("nonexistent_agent");
        keys.Should().BeEmpty();
    }

    [Fact]
    public async Task ListKeysAsync_WithMultipleKeys_ReturnsAllKeys()
    {
        await _store.WriteAsync("agent2", "key1", "v1");
        await _store.WriteAsync("agent2", "key2", "v2");
        await _store.WriteAsync("agent2", "key3", "v3");

        var keys = await _store.ListKeysAsync("agent2");
        keys.Should().HaveCount(3);
        keys.Should().Contain("key1");
        keys.Should().Contain("key2");
        keys.Should().Contain("key3");
    }

    [Fact]
    public async Task WriteAndRead_DailyKey_UsesDailySubfolder()
    {
        await _store.WriteAsync("agent3", "daily/2026-04-01", "daily memory");

        var result = await _store.ReadAsync("agent3", "daily/2026-04-01");
        result.Should().Be("daily memory");

        var expectedPath = Path.Combine(BotNexusHome.GetAgentWorkspacePath("agent3"), "memory", "daily", "2026-04-01.md");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAndRead_MemoryKey_UsesWorkspaceRoot()
    {
        await _store.WriteAsync("agent4", "MEMORY", "long-term memory");

        var result = await _store.ReadAsync("agent4", "MEMORY");
        result.Should().Be("long-term memory");

        var expectedPath = Path.Combine(BotNexusHome.GetAgentWorkspacePath("agent4"), "MEMORY.md");
        File.Exists(expectedPath).Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_FallsBackToLegacyTxtPath()
    {
        var legacyPath = Path.Combine(_legacyBasePath, "agent5", "memory");
        Directory.CreateDirectory(legacyPath);
        await File.WriteAllTextAsync(Path.Combine(legacyPath, "facts.txt"), "legacy facts");

        var result = await _store.ReadAsync("agent5", "facts");
        result.Should().Be("legacy facts");
    }

    [Fact]
    public async Task ReadAsync_FallsBackToLegacyMemoryFile()
    {
        var legacyPath = Path.Combine(_legacyBasePath, "agent6", "memory");
        Directory.CreateDirectory(legacyPath);
        await File.WriteAllTextAsync(Path.Combine(legacyPath, "MEMORY.txt"), "legacy memory");

        var result = await _store.ReadAsync("agent6", "MEMORY");
        result.Should().Be("legacy memory");
    }

    [Fact]
    public async Task DifferentAgents_HaveIsolatedMemory()
    {
        await _store.WriteAsync("agent_a", "shared_key", "agent_a content");
        await _store.WriteAsync("agent_b", "shared_key", "agent_b content");

        var resultA = await _store.ReadAsync("agent_a", "shared_key");
        var resultB = await _store.ReadAsync("agent_b", "shared_key");

        resultA.Should().Be("agent_a content");
        resultB.Should().Be("agent_b content");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeOverrideEnvVar, _originalHomeOverride);

        if (Directory.Exists(_tempHomePath))
            Directory.Delete(_tempHomePath, recursive: true);

        if (Directory.Exists(_legacyBasePath))
            Directory.Delete(_legacyBasePath, recursive: true);
    }
}
