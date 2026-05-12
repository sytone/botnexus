using System.Reflection;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Tests.Sessions;

public sealed class ListByChannelTests
{
    [Fact]
    public async Task FiltersByAgentAndChannel()
    {
        var store = new InMemorySessionStore();
        await SaveSessionAsync(store, "match", "agent-a", ChannelKey.From("web"), DateTimeOffset.UtcNow.AddMinutes(-3));
        await SaveSessionAsync(store, "wrong-agent", "agent-b", ChannelKey.From("web"), DateTimeOffset.UtcNow.AddMinutes(-2));
        await SaveSessionAsync(store, "wrong-channel", "agent-a", ChannelKey.From("telegram"), DateTimeOffset.UtcNow.AddMinutes(-1));

        var sessions = await InvokeListByChannelAsync(store, "agent-a", ChannelKey.From("web"));

        sessions.Select(s => s.SessionId.Value).ShouldBe(new[] { "match" });
    }

    [Fact]
    public async Task OrderedByCreatedAtDescending()
    {
        var store = new InMemorySessionStore();
        await SaveSessionAsync(store, "oldest", "agent-a", ChannelKey.From("web"), DateTimeOffset.UtcNow.AddMinutes(-30));
        await SaveSessionAsync(store, "middle", "agent-a", ChannelKey.From("web"), DateTimeOffset.UtcNow.AddMinutes(-20));
        await SaveSessionAsync(store, "newest", "agent-a", ChannelKey.From("web"), DateTimeOffset.UtcNow.AddMinutes(-10));

        var sessions = await InvokeListByChannelAsync(store, "agent-a", ChannelKey.From("web"));

        sessions.Select(s => s.SessionId.Value).ToList().ShouldBe(new[] { "newest", "middle", "oldest" });
        sessions.Select(s => s.CreatedAt).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact]
    public async Task SkipsNullChannelType()
    {
        var store = new InMemorySessionStore();
        await SaveSessionAsync(store, "null-channel", "agent-a", null, DateTimeOffset.UtcNow.AddMinutes(-2));
        await SaveSessionAsync(store, "web", "agent-a", ChannelKey.From("web"), DateTimeOffset.UtcNow.AddMinutes(-1));

        var sessions = await InvokeListByChannelAsync(store, "agent-a", ChannelKey.From("web"));

        sessions.Select(s => s.SessionId.Value).ShouldBe(new[] { "web" });
    }

    private static async Task SaveSessionAsync(
        ISessionStore store,
        string sessionId,
        string agentId,
        ChannelKey? channelType,
        DateTimeOffset createdAt)
    {
        var session = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            ChannelType = channelType,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

        await store.SaveAsync(session, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<GatewaySession>> InvokeListByChannelAsync(
        ISessionStore store,
        string agentId,
        ChannelKey channelType)
    {
        var method = store.GetType().GetMethod(
            "ListByChannelAsync",
            BindingFlags.Instance | BindingFlags.Public,
            [typeof(BotNexus.Domain.Primitives.AgentId), typeof(ChannelKey), typeof(CancellationToken)]);

        method.ShouldNotBeNull("ListByChannelAsync must exist on session store implementations.");
        var reflectionMethod = method ?? throw new InvalidOperationException("Expected ListByChannelAsync method.");
        var invocationResult = reflectionMethod.Invoke(store, [BotNexus.Domain.Primitives.AgentId.From(agentId), channelType, CancellationToken.None]);
        invocationResult.ShouldNotBeNull();
        var task = invocationResult as Task ?? throw new InvalidOperationException("Expected task result.");
        await task;

        var resultProperty = task.GetType().GetProperty("Result");
        resultProperty.ShouldNotBeNull();
        var sessions = resultProperty!.GetValue(task) as IReadOnlyList<GatewaySession>;
        sessions.ShouldNotBeNull();
        return sessions!;
    }
}
