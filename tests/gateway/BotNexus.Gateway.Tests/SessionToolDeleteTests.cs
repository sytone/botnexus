using BotNexus.Domain.Primitives;
using System.Text.Json;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Tools;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class SessionToolDeleteTests
{
    [Fact]
    public async Task Delete_SealedSession_Succeeds()
    {
        var store = new Mock<ISessionStore>();
        var session = CreateSession("s1", "agent-a", SessionStatus.Sealed);
        store.Setup(s => s.GetAsync(SessionId.From("s1"), It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.DeleteAsync(SessionId.From("s1"), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var tool = new SessionTool(store.Object, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", Args("delete", sessionId: "s1"));
        var text = ReadText(result);

        text.ShouldContain("s1");
        text.ShouldContain("deleted");
        store.Verify(s => s.DeleteAsync(SessionId.From("s1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_ExpiredSession_Succeeds()
    {
        var store = new Mock<ISessionStore>();
        var session = CreateSession("s1", "agent-a", SessionStatus.Expired);
        store.Setup(s => s.GetAsync(SessionId.From("s1"), It.IsAny<CancellationToken>())).ReturnsAsync(session);
        store.Setup(s => s.DeleteAsync(SessionId.From("s1"), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var tool = new SessionTool(store.Object, AgentId.From("agent-a"));

        var result = await tool.ExecuteAsync("call-1", Args("delete", sessionId: "s1"));
        var text = ReadText(result);

        text.ShouldContain("deleted");
    }

    [Fact]
    public async Task Delete_ActiveSession_Throws()
    {
        var store = new Mock<ISessionStore>();
        var session = CreateSession("s1", "agent-a", SessionStatus.Active);
        store.Setup(s => s.GetAsync(SessionId.From("s1"), It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var tool = new SessionTool(store.Object, AgentId.From("agent-a"));

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args("delete", sessionId: "s1"));

        await act.ShouldThrowAsync<InvalidOperationException>();
        store.Verify(s => s.DeleteAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_SuspendedSession_Throws()
    {
        var store = new Mock<ISessionStore>();
        var session = CreateSession("s1", "agent-a", SessionStatus.Suspended);
        store.Setup(s => s.GetAsync(SessionId.From("s1"), It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var tool = new SessionTool(store.Object, AgentId.From("agent-a"));

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args("delete", sessionId: "s1"));

        await act.ShouldThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Delete_NonexistentSession_Throws()
    {
        var store = new Mock<ISessionStore>();
        store.Setup(s => s.GetAsync(SessionId.From("missing"), It.IsAny<CancellationToken>())).ReturnsAsync((GatewaySession?)null);
        var tool = new SessionTool(store.Object, AgentId.From("agent-a"));

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args("delete", sessionId: "missing"));

        await act.ShouldThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Delete_OtherAgentSession_DeniedForOwnAccess()
    {
        var store = new Mock<ISessionStore>();
        var session = CreateSession("s1", "agent-b", SessionStatus.Sealed);
        store.Setup(s => s.GetAsync(SessionId.From("s1"), It.IsAny<CancellationToken>())).ReturnsAsync(session);
        var tool = new SessionTool(store.Object, AgentId.From("agent-a"));

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args("delete", sessionId: "s1"));

        await act.ShouldThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Delete_MissingSessionId_Throws()
    {
        var store = new Mock<ISessionStore>();
        var tool = new SessionTool(store.Object, AgentId.From("agent-a"));

        Func<Task> act = () => tool.ExecuteAsync("call-1", Args("delete"));

        await act.ShouldThrowAsync<ArgumentException>();
    }

    private static GatewaySession CreateSession(string sessionId, string agentId, SessionStatus status = SessionStatus.Active)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(agentId),
            ChannelType = ChannelKey.From("Web Chat"),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTimeOffset.UtcNow
        };
        session.Status = status;
        return session;
    }

    private static Dictionary<string, object?> Args(string action, string? sessionId = null)
    {
        var args = new Dictionary<string, object?> { ["action"] = action };
        if (sessionId is not null) args["sessionId"] = sessionId;
        return args;
    }

    private static string ReadText(AgentToolResult result)
        => result.Content.Single(c => c.Type == AgentToolContentType.Text).Value;
}
