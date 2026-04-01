using BotNexus.Agent;
using BotNexus.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Integration.Tests;

/// <summary>
/// SC-AWM-010: Memory store isolation between agents
/// Validates that different agents have completely isolated memory stores —
/// writes by one agent are never visible to another through the full MemoryStore.
/// </summary>
[CollectionDefinition("memory-isolation-e2e", DisableParallelization = true)]
public sealed class MemoryIsolationE2eCollection;

[Collection("memory-isolation-e2e")]
public sealed class MemoryStoreIsolationE2eTests : IDisposable
{
    private readonly string _testHomePath;
    private readonly string? _originalHomeOverride;
    private readonly MemoryStore _store;

    public MemoryStoreIsolationE2eTests()
    {
        _originalHomeOverride = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        _testHomePath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "memory-isolation-e2e",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testHomePath);
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _testHomePath);
        BotNexusHome.Initialize();
        _store = new MemoryStore(_testHomePath, NullLogger<MemoryStore>.Instance);
    }

    [Fact]
    public async Task AgentA_MemoryWrite_IsNotVisibleToAgentB()
    {
        BotNexusHome.InitializeAgentWorkspace("agent-alpha");
        BotNexusHome.InitializeAgentWorkspace("agent-beta");

        // Agent A saves a memory entry
        await _store.WriteAsync("agent-alpha", "secret-notes", "Alpha's private information about Project X");

        // Agent B tries to read with the same key
        var betaResult = await _store.ReadAsync("agent-beta", "secret-notes");
        betaResult.Should().BeNull("Agent B should not see Agent A's memory entries");

        // Agent A can still read its own
        var alphaResult = await _store.ReadAsync("agent-alpha", "secret-notes");
        alphaResult.Should().Be("Alpha's private information about Project X");
    }

    [Fact]
    public async Task AgentA_DailyMemory_IsIsolatedFromAgentB()
    {
        BotNexusHome.InitializeAgentWorkspace("agent-alpha");
        BotNexusHome.InitializeAgentWorkspace("agent-beta");

        // Both agents write daily memory with the same date key
        await _store.WriteAsync("agent-alpha", "daily/2026-04-01", "Alpha observed pattern X today");
        await _store.WriteAsync("agent-beta", "daily/2026-04-01", "Beta learned about topic Y");

        // Each agent only sees their own content
        var alphaDaily = await _store.ReadAsync("agent-alpha", "daily/2026-04-01");
        var betaDaily = await _store.ReadAsync("agent-beta", "daily/2026-04-01");

        alphaDaily.Should().Be("Alpha observed pattern X today");
        betaDaily.Should().Be("Beta learned about topic Y");
        alphaDaily.Should().NotBe(betaDaily);
    }

    [Fact]
    public async Task AgentA_ListKeys_DoesNotIncludeAgentB_Keys()
    {
        BotNexusHome.InitializeAgentWorkspace("agent-alpha");
        BotNexusHome.InitializeAgentWorkspace("agent-beta");

        await _store.WriteAsync("agent-alpha", "alpha-key-1", "content");
        await _store.WriteAsync("agent-alpha", "alpha-key-2", "content");
        await _store.WriteAsync("agent-beta", "beta-key-1", "content");

        var alphaKeys = await _store.ListKeysAsync("agent-alpha");
        var betaKeys = await _store.ListKeysAsync("agent-beta");

        alphaKeys.Should().Contain("alpha-key-1");
        alphaKeys.Should().Contain("alpha-key-2");
        alphaKeys.Should().NotContain("beta-key-1");

        betaKeys.Should().Contain("beta-key-1");
        betaKeys.Should().NotContain("alpha-key-1");
        betaKeys.Should().NotContain("alpha-key-2");
    }

    [Fact]
    public async Task AgentA_DeleteKey_DoesNotAffectAgentB_SameKey()
    {
        BotNexusHome.InitializeAgentWorkspace("agent-alpha");
        BotNexusHome.InitializeAgentWorkspace("agent-beta");

        await _store.WriteAsync("agent-alpha", "shared-name", "Alpha content");
        await _store.WriteAsync("agent-beta", "shared-name", "Beta content");

        // Delete alpha's key
        await _store.DeleteAsync("agent-alpha", "shared-name");

        var alphaResult = await _store.ReadAsync("agent-alpha", "shared-name");
        var betaResult = await _store.ReadAsync("agent-beta", "shared-name");

        alphaResult.Should().BeNull("Alpha's key should be deleted");
        betaResult.Should().Be("Beta content", "Beta's key should be unaffected");
    }

    [Fact]
    public async Task MEMORY_File_IsIsolatedPerAgent()
    {
        BotNexusHome.InitializeAgentWorkspace("agent-alpha");
        BotNexusHome.InitializeAgentWorkspace("agent-beta");

        await _store.WriteAsync("agent-alpha", "MEMORY", "# Alpha Long-Term Memory\n\nAlpha remembers X");
        await _store.WriteAsync("agent-beta", "MEMORY", "# Beta Long-Term Memory\n\nBeta remembers Y");

        var alphaMemory = await _store.ReadAsync("agent-alpha", "MEMORY");
        var betaMemory = await _store.ReadAsync("agent-beta", "MEMORY");

        alphaMemory.Should().Contain("Alpha remembers X");
        alphaMemory.Should().NotContain("Beta");
        betaMemory.Should().Contain("Beta remembers Y");
        betaMemory.Should().NotContain("Alpha");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _originalHomeOverride);
        if (Directory.Exists(_testHomePath))
        {
            try { Directory.Delete(_testHomePath, recursive: true); } catch { }
        }
    }
}
