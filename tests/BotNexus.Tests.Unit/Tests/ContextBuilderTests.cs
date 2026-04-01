using BotNexus.Core.Models;
using BotNexus.Agent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class ContextBuilderTests
{
    private readonly ContextBuilder _builder = new(NullLogger<ContextBuilder>.Instance);

    private static Core.Models.Session MakeSession(string key = "test:s1", string agentName = "agent")
        => new() { Key = key, AgentName = agentName };

    private static InboundMessage MakeMessage(string content = "hello")
        => new("telegram", "u1", "c1", content, DateTimeOffset.UtcNow, [], new Dictionary<string, object>());

    private static GenerationSettings DefaultSettings => new()
    {
        Model = "gpt-4o",
        MaxTokens = 4096,
        ContextWindowTokens = 10000,
        Temperature = 0.1
    };

    [Fact]
    public void Build_EmptySession_ReturnsOnlyUserMessage()
    {
        var session = MakeSession();
        var message = MakeMessage("hello");

        var result = _builder.Build(session, message, DefaultSettings);

        result.Should().HaveCount(1);
        result[0].Role.Should().Be("user");
        result[0].Content.Should().Be("hello");
    }

    [Fact]
    public void Build_WithHistory_IncludesHistoryAndNewMessage()
    {
        var session = MakeSession();
        session.AddEntry(new SessionEntry(MessageRole.User, "previous user message", DateTimeOffset.UtcNow));
        session.AddEntry(new SessionEntry(MessageRole.Assistant, "previous assistant reply", DateTimeOffset.UtcNow));

        var result = _builder.Build(session, MakeMessage("new message"), DefaultSettings);

        result.Should().HaveCount(3);
        result[^1].Content.Should().Be("new message");
    }

    [Fact]
    public void Build_LargeHistory_TrimsByContextWindow()
    {
        var session = MakeSession();
        var settings = new GenerationSettings
        {
            ContextWindowTokens = 10,
            MaxTokens = 100
        };

        for (int i = 0; i < 100; i++)
            session.AddEntry(new SessionEntry(MessageRole.User, $"message {i} with some extra content", DateTimeOffset.UtcNow));

        var result = _builder.Build(session, MakeMessage("new"), settings);

        // Should not include all 100 history entries due to budget
        result.Should().HaveCountLessThan(100);
        // The last message should be the new user message
        result[^1].Content.Should().Be("new");
    }

    [Fact]
    public void Build_MapsMessageRolesCorrectly()
    {
        var session = MakeSession();
        session.AddEntry(new SessionEntry(MessageRole.User, "user says", DateTimeOffset.UtcNow));
        session.AddEntry(new SessionEntry(MessageRole.Assistant, "assistant says", DateTimeOffset.UtcNow));
        session.AddEntry(new SessionEntry(MessageRole.Tool, "tool result", DateTimeOffset.UtcNow, ToolName: "shell"));

        var result = _builder.Build(session, MakeMessage("new"), DefaultSettings);

        result.Should().Contain(m => m.Role == "user");
        result.Should().Contain(m => m.Role == "assistant");
        result.Should().Contain(m => m.Role == "tool");
    }
}
