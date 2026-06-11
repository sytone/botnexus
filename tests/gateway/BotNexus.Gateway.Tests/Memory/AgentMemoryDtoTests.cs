using BotNexus.Gateway.Contracts.Memory;

namespace BotNexus.Gateway.Tests.Memory;

public sealed class AgentMemoryDtoTests
{
    [Fact]
    public void AgentMemoryPromptRequest_DefaultValues()
    {
        var request = new AgentMemoryPromptRequest("agent-1");

        Assert.Equal("agent-1", request.AgentId);
        Assert.Null(request.SessionId);
        Assert.Null(request.ConversationId);
        Assert.Equal(4000, request.MaxTokenBudget);
    }

    [Fact]
    public void AgentMemoryPromptRequest_WithAllParameters()
    {
        var request = new AgentMemoryPromptRequest(
            "agent-1",
            SessionId: "session-1",
            ConversationId: "conv-1",
            MaxTokenBudget: 8000);

        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal("session-1", request.SessionId);
        Assert.Equal("conv-1", request.ConversationId);
        Assert.Equal(8000, request.MaxTokenBudget);
    }

    [Fact]
    public void AgentMemoryContext_Empty_HasNoContent()
    {
        var context = AgentMemoryContext.Empty;

        Assert.Null(context.LongTermMemory);
        Assert.Empty(context.DailyNotes);
        Assert.Equal(0, context.ApproximateTokenCount);
    }

    [Fact]
    public void AgentMemoryContext_WithContent()
    {
        var notes = new[]
        {
            new AgentMemoryDailyNote(new DateOnly(2026, 6, 10), "Today's note"),
            new AgentMemoryDailyNote(new DateOnly(2026, 6, 9), "Yesterday's note")
        };

        var context = new AgentMemoryContext("Long-term memory content", notes, 150);

        Assert.Equal("Long-term memory content", context.LongTermMemory);
        Assert.Equal(2, context.DailyNotes.Count);
        Assert.Equal(150, context.ApproximateTokenCount);
    }

    [Fact]
    public void AgentMemoryDailyNote_RecordEquality()
    {
        var note1 = new AgentMemoryDailyNote(new DateOnly(2026, 6, 10), "Content");
        var note2 = new AgentMemoryDailyNote(new DateOnly(2026, 6, 10), "Content");

        Assert.Equal(note1, note2);
        Assert.Equal(note1.GetHashCode(), note2.GetHashCode());
    }

    [Fact]
    public void AgentMemorySaveRequest_DefaultValues()
    {
        var request = new AgentMemorySaveRequest("agent-1", "Some content", "conversation");

        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal("Some content", request.Content);
        Assert.Equal("conversation", request.SourceType);
        Assert.Null(request.SessionId);
        Assert.Null(request.TurnIndex);
        Assert.Null(request.Tags);
        Assert.Null(request.ExpiresAt);
    }

    [Fact]
    public void AgentMemorySaveRequest_WithAllParameters()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(7);
        var tags = new[] { "important", "architecture" };

        var request = new AgentMemorySaveRequest(
            "agent-1",
            "Content",
            "manual",
            SessionId: "session-1",
            TurnIndex: 5,
            Tags: tags,
            ExpiresAt: expires);

        Assert.Equal("session-1", request.SessionId);
        Assert.Equal(5, request.TurnIndex);
        Assert.Equal(tags, request.Tags);
        Assert.Equal(expires, request.ExpiresAt);
    }

    [Fact]
    public void AgentMemorySearchRequest_DefaultValues()
    {
        var request = new AgentMemorySearchRequest("agent-1", "find something");

        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal("find something", request.Query);
        Assert.Equal(10, request.TopK);
        Assert.Null(request.Filter);
    }

    [Fact]
    public void AgentMemorySearchResult_RecordEquality()
    {
        var created = DateTimeOffset.UtcNow;
        var result1 = new AgentMemorySearchResult("id-1", "Content", "conversation", "session-1", created, 0.95);
        var result2 = new AgentMemorySearchResult("id-1", "Content", "conversation", "session-1", created, 0.95);

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void AgentMemorySearchResult_DefaultRelevanceScore()
    {
        var result = new AgentMemorySearchResult("id-1", "Content", "manual", null, DateTimeOffset.UtcNow);

        Assert.Equal(0.0, result.RelevanceScore);
        Assert.Null(result.Tags);
    }

    [Fact]
    public void AgentMemorySessionEvent_DefaultValues()
    {
        var evt = new AgentMemorySessionEvent("agent-1", "session-1");

        Assert.Equal("agent-1", evt.AgentId);
        Assert.Equal("session-1", evt.SessionId);
        Assert.Null(evt.ConversationId);
        Assert.Null(evt.EndedAt);
        Assert.Equal(0, evt.TurnCount);
    }

    [Fact]
    public void AgentMemoryConsolidateRequest_DefaultValues()
    {
        var request = new AgentMemoryConsolidateRequest("agent-1");

        Assert.Equal("agent-1", request.AgentId);
        Assert.Equal(14, request.LookbackDays);
        Assert.Equal(50_000, request.MaxContentChars);
        Assert.False(request.DryRun);
    }

    [Fact]
    public void AgentMemoryConsolidateRequest_WithDryRun()
    {
        var request = new AgentMemoryConsolidateRequest("agent-1", LookbackDays: 7, MaxContentChars: 25_000, DryRun: true);

        Assert.Equal(7, request.LookbackDays);
        Assert.Equal(25_000, request.MaxContentChars);
        Assert.True(request.DryRun);
    }

    [Fact]
    public void AgentMemorySearchFilter_DefaultValues()
    {
        var filter = new AgentMemorySearchFilter();

        Assert.Null(filter.SourceType);
        Assert.Null(filter.SessionId);
        Assert.Null(filter.AfterDate);
        Assert.Null(filter.BeforeDate);
        Assert.Null(filter.Tags);
    }

    [Fact]
    public void AgentMemorySearchFilter_WithAllProperties()
    {
        var after = DateTimeOffset.UtcNow.AddDays(-7);
        var before = DateTimeOffset.UtcNow;
        var tags = new[] { "tag1" };

        var filter = new AgentMemorySearchFilter
        {
            SourceType = "conversation",
            SessionId = "session-1",
            AfterDate = after,
            BeforeDate = before,
            Tags = tags
        };

        Assert.Equal("conversation", filter.SourceType);
        Assert.Equal("session-1", filter.SessionId);
        Assert.Equal(after, filter.AfterDate);
        Assert.Equal(before, filter.BeforeDate);
        Assert.Equal(tags, filter.Tags);
    }
}
