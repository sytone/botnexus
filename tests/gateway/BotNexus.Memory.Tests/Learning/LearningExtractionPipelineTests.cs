using BotNexus.Memory.Learning;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Memory.Tests.Learning;

public sealed class LearningExtractionPipelineTests
{
    private static LearningExtractionPipeline CreatePipeline(IReadOnlyList<KnowledgeRoutingRule>? rules = null)
        => new(rules ?? [], NullLogger.Instance);

    [Fact]
    public async Task ExtractAsync_ConversationEntry_ExtractsDurableKnowledge()
    {
        var pipeline = CreatePipeline();
        var entries = new List<MemoryEntry>
        {
            new()
            {
                Id = "1",
                AgentId = "agent1",
                SessionId = "session1",
                TurnIndex = 0,
                SourceType = "conversation",
                Content = "User: Should we use PostgreSQL or SQLite?\nAssistant: We decided to go with SQLite because it requires no external dependencies and performs well for our use case.",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var results = await pipeline.ExtractAsync(entries);

        Assert.Single(results);
        Assert.Equal(KnowledgeCategory.Decision, results[0].Category);
        Assert.Equal("session1", results[0].SourceSessionId);
    }

    [Fact]
    public async Task ExtractAsync_TransientEntry_ReturnsEmpty()
    {
        var pipeline = CreatePipeline();
        var entries = new List<MemoryEntry>
        {
            new()
            {
                Id = "1",
                AgentId = "agent1",
                SessionId = "session1",
                TurnIndex = 0,
                SourceType = "conversation",
                Content = "User: hi\nAssistant: hello!",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var results = await pipeline.ExtractAsync(entries);
        Assert.Empty(results);
    }

    [Fact]
    public async Task ExtractAsync_NonConversationEntry_Skipped()
    {
        var pipeline = CreatePipeline();
        var entries = new List<MemoryEntry>
        {
            new()
            {
                Id = "1",
                AgentId = "agent1",
                SourceType = "dreaming",
                Content = "Some consolidated note",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var results = await pipeline.ExtractAsync(entries);
        Assert.Empty(results);
    }

    [Fact]
    public async Task ExtractAsync_WithRoutingRules_AppliesRouting()
    {
        var rules = new List<KnowledgeRoutingRule>
        {
            new() { Category = KnowledgeCategory.Decision, MinConfidence = 0.3, TargetStore = "platform-decisions" },
        };
        var pipeline = CreatePipeline(rules);

        var entries = new List<MemoryEntry>
        {
            new()
            {
                Id = "1",
                AgentId = "agent1",
                SessionId = "s1",
                TurnIndex = 0,
                SourceType = "conversation",
                Content = "User: What database should we use?\nAssistant: We decided to use SQLite for the store because it is lightweight and requires no setup.",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var results = await pipeline.ExtractAsync(entries);

        Assert.Single(results);
        Assert.Equal("platform-decisions", results[0].TargetStore);
    }

    [Fact]
    public async Task ExtractAsync_MalformedContent_Skipped()
    {
        var pipeline = CreatePipeline();
        var entries = new List<MemoryEntry>
        {
            new()
            {
                Id = "1",
                AgentId = "agent1",
                SourceType = "conversation",
                Content = "This is not in the expected format at all",
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        var results = await pipeline.ExtractAsync(entries);
        Assert.Empty(results);
    }

    [Fact]
    public void TryParseConversationEntry_ValidFormat_ReturnsTrueAndParts()
    {
        var content = "User: Hello world\nAssistant: Hi there";
        Assert.True(LearningExtractionPipeline.TryParseConversationEntry(content, out var user, out var assistant));
        Assert.Equal("Hello world", user);
        Assert.Equal("Hi there", assistant);
    }

    [Fact]
    public void TryParseConversationEntry_InvalidFormat_ReturnsFalse()
    {
        Assert.False(LearningExtractionPipeline.TryParseConversationEntry("random text", out _, out _));
    }
}
