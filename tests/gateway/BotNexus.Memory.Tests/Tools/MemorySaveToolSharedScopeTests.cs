using System.Text.Json;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Models;
using BotNexus.Memory.Tools;
using Moq;

namespace BotNexus.Memory.Tests.Tools;

public sealed class MemorySaveToolSharedScopeTests
{
    private const string AgentId = "test-agent";

    [Fact]
    public async Task SaveToSharedStore_ValidatesWriteAccess()
    {
        var agentMemory = new Mock<IAgentMemory>();
        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();
        sharedRegistry.Setup(r => r.CanWrite(AgentId, "read-only-store")).Returns(false);

        var tool = new MemorySaveTool(agentMemory.Object, AgentId, sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["content"] = "some knowledge",
            ["store"] = "read-only-store"
        });

        result.Content[0].Value.ShouldContain("Access denied");
    }

    [Fact]
    public async Task SaveToSharedStore_InsertsEntryWhenAccessGranted()
    {
        var agentMemory = new Mock<IAgentMemory>();
        var sharedStore = new Mock<IMemoryStore>();
        sharedStore.Setup(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry e, CancellationToken _) => e);

        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();
        sharedRegistry.Setup(r => r.CanWrite(AgentId, "team-knowledge")).Returns(true);
        sharedRegistry.Setup(r => r.GetStore("team-knowledge")).Returns(sharedStore.Object);

        var tool = new MemorySaveTool(agentMemory.Object, AgentId, sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["content"] = "important pattern",
            ["store"] = "team-knowledge",
            ["category"] = "pattern"
        });

        result.Content[0].Value.ShouldContain("Saved memory note to shared store 'team-knowledge'");
        sharedStore.Verify(s => s.InsertAsync(
            It.Is<MemoryEntry>(e =>
                e.Content == "important pattern" &&
                e.SourceType == "pattern" &&
                e.AgentId == AgentId &&
                e.MetadataJson != null &&
                e.MetadataJson.Contains("category:pattern")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveWithTags_IncludesTagsInMetadata()
    {
        var agentMemory = new Mock<IAgentMemory>();
        var sharedStore = new Mock<IMemoryStore>();
        sharedStore.Setup(s => s.InsertAsync(It.IsAny<MemoryEntry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((MemoryEntry e, CancellationToken _) => e);

        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();
        sharedRegistry.Setup(r => r.CanWrite(AgentId, "store1")).Returns(true);
        sharedRegistry.Setup(r => r.GetStore("store1")).Returns(sharedStore.Object);

        var tagsJson = JsonDocument.Parse("[\"botnexus\", \"architecture\"]").RootElement.Clone();
        var tool = new MemorySaveTool(agentMemory.Object, AgentId, sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["content"] = "design decision",
            ["store"] = "store1",
            ["category"] = "decision",
            ["tags"] = tagsJson
        });

        result.Content[0].Value.ShouldContain("Saved memory note to shared store");
        sharedStore.Verify(s => s.InsertAsync(
            It.Is<MemoryEntry>(e =>
                e.MetadataJson != null &&
                e.MetadataJson.Contains("category:decision") &&
                e.MetadataJson.Contains("botnexus")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveWithoutStore_UsesDefaultAgentMemory()
    {
        var agentMemory = new Mock<IAgentMemory>();
        var sharedRegistry = new Mock<ISharedMemoryStoreRegistry>();

        var tool = new MemorySaveTool(agentMemory.Object, AgentId, sharedRegistry.Object);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["content"] = "local note"
        });

        result.Content[0].Value.ShouldContain("default memory target");
        agentMemory.Verify(m => m.SaveAsync(
            It.Is<AgentMemorySaveRequest>(r => r.Content == "local note" && r.AgentId == AgentId),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveWithCategoryAndTags_SetsTagsOnOwnSave()
    {
        var agentMemory = new Mock<IAgentMemory>();

        var tool = new MemorySaveTool(agentMemory.Object, AgentId);
        var tagsJson = JsonDocument.Parse("[\"tag1\"]").RootElement.Clone();
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["content"] = "local note with tags",
            ["category"] = "fact",
            ["tags"] = tagsJson
        });

        agentMemory.Verify(m => m.SaveAsync(
            It.Is<AgentMemorySaveRequest>(r =>
                r.Tags != null &&
                r.Tags.Contains("category:fact") &&
                r.Tags.Contains("tag1")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoSharedRegistry_ReturnsNotConfigured()
    {
        var agentMemory = new Mock<IAgentMemory>();
        var tool = new MemorySaveTool(agentMemory.Object, AgentId, sharedRegistry: null);
        var result = await tool.ExecuteAsync("tc1", new Dictionary<string, object?>
        {
            ["content"] = "something",
            ["store"] = "nonexistent"
        });

        result.Content[0].Value.ShouldContain("not configured");
    }
}
