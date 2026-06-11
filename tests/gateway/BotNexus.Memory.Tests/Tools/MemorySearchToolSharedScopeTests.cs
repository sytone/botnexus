using System.Text.Json;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Models;
using BotNexus.Memory.Tools;
using Moq;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemorySearchToolSharedScopeTests
{
    private const string AgentId = "test-agent";

    [Fact]
    public async Task ScopeOwn_SearchesOnlyAgentMemory()
    {
        var agentMemory = new Mock<IAgentMemory>();
        agentMemory.Setup(m => m.SearchAsync(It.IsAny<AgentMemorySearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentMemorySearchResult>
            {
                new("1", "own result", "tool", null, DateTimeOffset.UtcNow)
            });

        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();
        sharedRegistry.Setup(r => r.GetReadableStores(AgentId)).Returns(["shared-store"]);

        var tool = new MemorySearchTool(agentMemory.Object, AgentId, sharedRegistry: sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["scope"] = "own"
        });

        result.Content[0].Value.ShouldContain("own result");
        sharedRegistry.Verify(r => r.GetStore(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ScopeShared_SearchesOnlySharedStores()
    {
        var agentMemory = new Mock<IAgentMemory>();
        var sharedStore = new Mock<IMemoryStore>();
        sharedStore.Setup(s => s.SearchAsync("test", 10, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>
            {
                new() { Id = "s1", AgentId = "other", Content = "shared result", SourceType = "tool", CreatedAt = DateTimeOffset.UtcNow }
            });

        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();
        sharedRegistry.Setup(r => r.GetReadableStores(AgentId)).Returns(["platform-knowledge"]);
        sharedRegistry.Setup(r => r.GetStore("platform-knowledge")).Returns(sharedStore.Object);

        var tool = new MemorySearchTool(agentMemory.Object, AgentId, sharedRegistry: sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["scope"] = "shared"
        });

        result.Content[0].Value.ShouldContain("shared result");
        result.Content[0].Value.ShouldContain("shared:platform-knowledge");
        agentMemory.Verify(m => m.SearchAsync(It.IsAny<AgentMemorySearchRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScopeAll_SearchesBothOwnAndShared()
    {
        var agentMemory = new Mock<IAgentMemory>();
        agentMemory.Setup(m => m.SearchAsync(It.IsAny<AgentMemorySearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentMemorySearchResult>
            {
                new("1", "own data", "tool", null, DateTimeOffset.UtcNow.AddMinutes(-5))
            });

        var sharedStore = new Mock<IMemoryStore>();
        sharedStore.Setup(s => s.SearchAsync("test", 10, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>
            {
                new() { Id = "s1", AgentId = "other", Content = "shared data", SourceType = "tool", CreatedAt = DateTimeOffset.UtcNow }
            });

        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();
        sharedRegistry.Setup(r => r.GetReadableStores(AgentId)).Returns(["knowledge"]);
        sharedRegistry.Setup(r => r.GetStore("knowledge")).Returns(sharedStore.Object);

        var tool = new MemorySearchTool(agentMemory.Object, AgentId, sharedRegistry: sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["scope"] = "all"
        });

        result.Content[0].Value.ShouldContain("own data");
        result.Content[0].Value.ShouldContain("shared data");
    }

    [Fact]
    public async Task SpecificStore_ValidatesReadAccess()
    {
        var agentMemory = new Mock<IAgentMemory>();
        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();
        sharedRegistry.Setup(r => r.CanRead(AgentId, "secret-store")).Returns(false);

        var tool = new MemorySearchTool(agentMemory.Object, AgentId, sharedRegistry: sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["store"] = "secret-store"
        });

        result.Content[0].Value.ShouldContain("Access denied");
    }

    [Fact]
    public async Task SpecificStore_ReturnsResultsWhenAccessGranted()
    {
        var agentMemory = new Mock<IAgentMemory>();
        var sharedStore = new Mock<IMemoryStore>();
        sharedStore.Setup(s => s.SearchAsync("patterns", 10, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>
            {
                new() { Id = "p1", AgentId = "other", Content = "a pattern", SourceType = "decision", CreatedAt = DateTimeOffset.UtcNow }
            });

        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();
        sharedRegistry.Setup(r => r.CanRead(AgentId, "patterns-store")).Returns(true);
        sharedRegistry.Setup(r => r.GetStore("patterns-store")).Returns(sharedStore.Object);

        var tool = new MemorySearchTool(agentMemory.Object, AgentId, sharedRegistry: sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["query"] = "patterns",
            ["store"] = "patterns-store"
        });

        result.Content[0].Value.ShouldContain("a pattern");
    }

    [Fact]
    public async Task NoSharedRegistry_SharedScopeReturnsEmpty()
    {
        var agentMemory = new Mock<IAgentMemory>();
        agentMemory.Setup(m => m.SearchAsync(It.IsAny<AgentMemorySearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentMemorySearchResult>());

        var tool = new MemorySearchTool(agentMemory.Object, AgentId, sharedRegistry: null);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["query"] = "test",
            ["scope"] = "shared"
        });

        result.Content[0].Value.ShouldContain("No matching memories found");
    }

    [Fact]
    public async Task DefaultScope_IsAll()
    {
        var agentMemory = new Mock<IAgentMemory>();
        agentMemory.Setup(m => m.SearchAsync(It.IsAny<AgentMemorySearchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentMemorySearchResult>
            {
                new("1", "default result", "tool", null, DateTimeOffset.UtcNow)
            });

        var sharedStore = new Mock<IMemoryStore>();
        sharedStore.Setup(s => s.SearchAsync("test", 10, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemoryEntry>());

        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();
        sharedRegistry.Setup(r => r.GetReadableStores(AgentId)).Returns(["store1"]);
        sharedRegistry.Setup(r => r.GetStore("store1")).Returns(sharedStore.Object);

        // No scope parameter — should default to "all" (searches both)
        var tool = new MemorySearchTool(agentMemory.Object, AgentId, sharedRegistry: sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["query"] = "test"
        });

        // Should have searched both own and shared
        agentMemory.Verify(m => m.SearchAsync(It.IsAny<AgentMemorySearchRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        sharedStore.Verify(s => s.SearchAsync("test", 10, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
