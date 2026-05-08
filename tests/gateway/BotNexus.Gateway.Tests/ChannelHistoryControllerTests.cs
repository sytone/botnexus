using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

public sealed class ChannelHistoryControllerTests
{
    [Fact]
    public async Task GetHistory_WithoutCursor_ReturnsOldestFirstAndCrossSessionCursor()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(CreateSession(
            "s-new",
            "agent-a",
            "web chat",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "n0", "n1", "n2"));
        await store.SaveAsync(CreateSession(
            "s-old",
            "agent-a",
            "web chat",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            "o0", "o1"));

        var controller = new ChannelHistoryController(store);

        var result = await controller.GetHistory("web chat", "agent-a", limit: 4, cancellationToken: CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as ChannelHistoryResponse;
        payload.ShouldNotBeNull();
        payload!.Messages.Select(message => message.Content).ShouldBe(new[] { "o1", "n0", "n1", "n2" });
        payload.NextCursor.ShouldBe("s-old:1");
        payload.HasMore.ShouldBeTrue();
        payload.SessionBoundaries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task GetHistory_WithZeroCursor_MovesToOlderSession()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(CreateSession(
            "s-new",
            "agent-a",
            "web chat",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "n0", "n1"));
        await store.SaveAsync(CreateSession(
            "s-old",
            "agent-a",
            "web chat",
            DateTimeOffset.UtcNow.AddMinutes(-5),
            "o0", "o1"));

        var controller = new ChannelHistoryController(store);

        var result = await controller.GetHistory("web chat", "agent-a", cursor: "s-new:0", limit: 10, cancellationToken: CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as ChannelHistoryResponse;
        payload.ShouldNotBeNull();
        payload!.Messages.Select(message => message.Content).ShouldBe(new[] { "o0", "o1" });
        payload.HasMore.ShouldBeFalse();
        payload.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task GetHistory_WithMalformedCursor_ReturnsBadRequest()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(CreateSession(
            "s-new",
            "agent-a",
            "web chat",
            DateTimeOffset.UtcNow,
            "n0"));

        var controller = new ChannelHistoryController(store);

        var result = await controller.GetHistory("web chat", "agent-a", cursor: "bad-cursor", cancellationToken: CancellationToken.None);

        result.Result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetHistory_WithWebChatAlias_FindsSignalrSessionHistory()
    {
        var store = new InMemorySessionStore();
        await store.SaveAsync(CreateSession(
            "s-signalr",
            "agent-a",
            "signalr",
            DateTimeOffset.UtcNow,
            "hello"));

        var controller = new ChannelHistoryController(store);

        var result = await controller.GetHistory("web chat", "agent-a", cancellationToken: CancellationToken.None);

        var payload = (result.Result as OkObjectResult)?.Value as ChannelHistoryResponse;
        payload.ShouldNotBeNull();
        payload!.Messages.ShouldHaveSingleItem().Content.ShouldBe("hello");
    }

    private static GatewaySession CreateSession(
        string sessionId,
        string agentId,
        string channelType,
        DateTimeOffset createdAt,
        params string[] messages)
    {
        var session = new GatewaySession
        {
            SessionId = sessionId,
            AgentId = agentId,
            ChannelType = channelType,
            CreatedAt = createdAt
        };
        foreach (var message in messages)
            session.AddEntry(new SessionEntry { Role = MessageRole.User, Content = message });

        return session;
    }
}

