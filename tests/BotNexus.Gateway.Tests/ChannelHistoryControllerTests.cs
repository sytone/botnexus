using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
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
        payload.Should().NotBeNull();
        payload!.Messages.Select(message => message.Content).Should().Equal("o1", "n0", "n1", "n2");
        payload.NextCursor.Should().Be("s-old:1");
        payload.HasMore.Should().BeTrue();
        payload.SessionBoundaries.Should().ContainSingle();
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
        payload.Should().NotBeNull();
        payload!.Messages.Select(message => message.Content).Should().Equal("o0", "o1");
        payload.HasMore.Should().BeFalse();
        payload.NextCursor.Should().BeNull();
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

        result.Result.Should().BeOfType<BadRequestObjectResult>();
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

