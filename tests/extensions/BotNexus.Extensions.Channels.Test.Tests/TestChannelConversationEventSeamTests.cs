using System.Collections.Immutable;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Test.Tests;

/// <summary>
/// Real-collaborator seam coverage for conversation events (#2092). The production publisher,
/// routing contract, and opt-in test channel are all real; only the external transport is replaced
/// by the test channel's in-memory recorder.
/// </summary>
public sealed class TestChannelConversationEventSeamTests
{
    [Fact]
    public async Task Publisher_RoutesOrderedTypedEventsToEligibleTestChannelBindingExactlyOnce()
    {
        var adapter = new TestChannelAdapter(
            NullLogger<TestChannelAdapter>.Instance,
            Options.Create(new TestChannelOptions { ChannelId = "telegram" }));
        await using var publisher = new ConversationEventPublisher([adapter]);

        var agentId = AgentId.From("probe");
        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();
        var eligibleBindingId = BindingId.Create();
        var bindings = ImmutableArray.Create(
            new ConversationBindingSnapshot(
                eligibleBindingId,
                ChannelKey.From("telegram"),
                AdapterId: null,
                ChannelAddress.From("chat-100"),
                BindingMode.Interactive,
                ThreadingMode.Single),
            new ConversationBindingSnapshot(
                BindingId.Create(),
                ChannelKey.From("telegram"),
                AdapterId: null,
                ChannelAddress.From("muted-chat"),
                BindingMode.Muted,
                ThreadingMode.Single),
            new ConversationBindingSnapshot(
                BindingId.Create(),
                ChannelKey.From("signalr"),
                AdapterId: null,
                ChannelAddress.From("portal"),
                BindingMode.Interactive,
                ThreadingMode.Single));

        AgentStreamEvent[] streamEvents =
        [
            new()
            {
                Type = AgentStreamEventType.ContentDelta,
                ContentDelta = "hello ",
                AgentId = agentId,
                ConversationId = conversationId,
                SessionId = sessionId,
            },
            new()
            {
                Type = AgentStreamEventType.ToolStart,
                ToolCallId = "call-1",
                ToolName = "read",
                ToolArgs = new Dictionary<string, object?> { ["path"] = "README.md" },
                AgentId = agentId,
                ConversationId = conversationId,
                SessionId = sessionId,
            },
            new()
            {
                Type = AgentStreamEventType.ToolEnd,
                ToolCallId = "call-1",
                ToolName = "read",
                ToolResult = "done",
                ToolIsError = false,
                AgentId = agentId,
                ConversationId = conversationId,
                SessionId = sessionId,
            },
            new()
            {
                Type = AgentStreamEventType.ContentDelta,
                ContentDelta = "world",
                AgentId = agentId,
                ConversationId = conversationId,
                SessionId = sessionId,
            },
        ];

        foreach (var streamEvent in streamEvents)
        {
            var accepted = await publisher.PublishAsync(new ConversationAgentEvent
            {
                AgentId = agentId,
                ConversationId = conversationId,
                SessionId = sessionId,
                Origin = new ConversationEventOrigin(eligibleBindingId, UserId.From("jon"), "request-7"),
                Bindings = bindings,
                StreamEvent = streamEvent,
            });
            accepted.ShouldBeTrue();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await publisher.WaitForDrainAsync(timeout.Token);

        var captured = adapter.GetConversationEvents("chat-100");
        captured.Count.ShouldBe(streamEvents.Length);
        captured.Select(item => item.Sequence).ShouldBeInOrder();
        captured.Select(item => item.StreamEvent.Type).ShouldBe(streamEvents.Select(item => item.Type));
        captured.Select(item => item.StreamEvent).ShouldBe(streamEvents);
        captured.ShouldAllBe(item => item.AgentId == agentId);
        captured.ShouldAllBe(item => item.ConversationId == conversationId);
        captured.ShouldAllBe(item => item.SessionId == sessionId);
        captured.ShouldAllBe(item => item.BindingId == eligibleBindingId);
        captured.ShouldAllBe(item => item.ChannelRequestId == "request-7");
        captured.ShouldAllBe(item => item.Address == "chat-100");

        captured[1].StreamEvent.ToolArgs!["path"].ShouldBe("README.md");
        captured[2].StreamEvent.ToolResult.ShouldBe("done");
        adapter.GetConversationEvents("muted-chat").ShouldBeEmpty();
        adapter.GetAllConversationEvents().Count.ShouldBe(streamEvents.Length);
    }
}
