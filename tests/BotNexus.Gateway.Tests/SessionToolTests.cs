using System.Text.Json;
using BotNexus.AgentCore.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Tools;
using FluentAssertions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class SessionToolTests
{
    [Fact]
    public async Task List_OwnAccess_ReturnsOnlyOwnSessions()
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.ListAsync("agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateSession("s1", "agent-a"), CreateSession("s2", "agent-a")]);
        var tool = new SessionTool(store.Object, "agent-a");

        var result = await tool.ExecuteAsync("call-1", Args("list"));
        var text = ReadText(result);

        text.Should().Contain("s1");
        text.Should().Contain("s2");
        store.Verify(s => s.ListAsync("agent-a", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task List_OwnAccess_DeniesOtherAgent()
    {
        var store = new Mock<ISessionStore>();
        var tool = new SessionTool(store.Object, "agent-a");

        var act = () => tool.ExecuteAsync("call-1", Args("list", agentId: "agent-b"));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task List_AllowlistAccess_AllowsConfiguredAgent()
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.ListAsync("agent-b", It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateSession("s1", "agent-b")]);
        var tool = new SessionTool(store.Object, "agent-a", SessionAccessLevel.Allowlist, ["agent-b"]);

        var result = await tool.ExecuteAsync("call-1", Args("list", agentId: "agent-b"));
        var text = ReadText(result);

        text.Should().Contain("s1");
    }

    [Fact]
    public async Task List_AllowlistAccess_DeniesNonAllowedAgent()
    {
        var store = new Mock<ISessionStore>();
        var tool = new SessionTool(store.Object, "agent-a", SessionAccessLevel.Allowlist, ["agent-b"]);

        var act = () => tool.ExecuteAsync("call-1", Args("list", agentId: "agent-c"));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task List_AllAccess_AllowsAnyAgent()
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.ListAsync("agent-x", It.IsAny<CancellationToken>()))
            .ReturnsAsync([CreateSession("s1", "agent-x")]);
        var tool = new SessionTool(store.Object, "agent-a", SessionAccessLevel.All);

        var result = await tool.ExecuteAsync("call-1", Args("list", agentId: "agent-x"));
        var text = ReadText(result);

        text.Should().Contain("s1");
    }

    [Fact]
    public async Task Get_ReturnsSessionMetadata()
    {
        var store = new Mock<ISessionStore>();
        var session = CreateSession("s1", "agent-a");
        store.Setup(s => s.GetAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var tool = new SessionTool(store.Object, "agent-a");

        var result = await tool.ExecuteAsync("call-1", Args("get", sessionId: "s1"));
        var text = ReadText(result);

        text.Should().Contain("s1");
        text.Should().Contain("agent-a");
    }

    [Fact]
    public async Task Get_NonexistentSession_Throws()
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync("missing", It.IsAny<CancellationToken>())).ReturnsAsync((GatewaySession?)null);
        var tool = new SessionTool(store.Object, "agent-a");

        var act = () => tool.ExecuteAsync("call-1", Args("get", sessionId: "missing"));

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Get_OtherAgentSession_DeniedForOwnAccess()
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync("s1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSession("s1", "agent-b"));
        var tool = new SessionTool(store.Object, "agent-a");

        var act = () => tool.ExecuteAsync("call-1", Args("get", sessionId: "s1"));

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task History_ReturnsPaginatedMessages()
    {
        var store = new Mock<ISessionStore>();
        var session = CreateSession("s1", "agent-a");
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "Hello" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "Hi there!" });
        store.Setup(s => s.GetAsync("s1", It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var tool = new SessionTool(store.Object, "agent-a");

        var result = await tool.ExecuteAsync("call-1", Args("history", sessionId: "s1"));
        var text = ReadText(result);

        text.Should().Contain("Hello");
        text.Should().Contain("Hi there!");
        text.Should().Contain("\"totalCount\":2");
    }

    [Fact]
    public async Task Search_FindsMatchingMessages()
    {
        var store = new Mock<ISessionStore>();
        var session = CreateSession("s1", "agent-a");
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "Tell me about weather in Seattle" });
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "Seattle has mild winters." });
        store.Setup(s => s.ListAsync("agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([session]);
        var tool = new SessionTool(store.Object, "agent-a");

        var result = await tool.ExecuteAsync("call-1", Args("search", query: "Seattle"));
        var text = ReadText(result);

        text.Should().Contain("Seattle");
        text.Should().Contain("s1");
    }

    [Fact]
    public async Task Search_NoMatches_ReturnsEmpty()
    {
        var store = new Mock<ISessionStore>();
        var session = CreateSession("s1", "agent-a");
        session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = "Hello world" });
        store.Setup(s => s.ListAsync("agent-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync([session]);
        var tool = new SessionTool(store.Object, "agent-a");

        var result = await tool.ExecuteAsync("call-1", Args("search", query: "nonexistent"));
        var text = ReadText(result);

        text.Should().Be("[]");
    }

    private static GatewaySession CreateSession(string sessionId, string agentId)
        => new()
        {
            SessionId = sessionId,
            AgentId = agentId,
            ChannelType = ChannelKey.From("Web Chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static Dictionary<string, object?> Args(string action, string? sessionId = null, string? agentId = null, string? query = null)
    {
        var args = new Dictionary<string, object?> { ["action"] = action };
        if (sessionId is not null) args["sessionId"] = sessionId;
        if (agentId is not null) args["agentId"] = agentId;
        if (query is not null) args["query"] = query;
        return args;
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}


