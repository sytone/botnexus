using System.Collections.Immutable;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Channels.Tests.Events;

public sealed class ConversationEventStreamRoutingTests
{
    [Fact]
    public void GetTargets_FiltersByChannelAdapterAndMute_AndKeepsCorrelationOnlyOnOrigin()
    {
        var originId = BindingId.From("origin");
        var observerId = BindingId.From("observer");
        var streamEvent = new ConversationAgentEvent
        {
            AgentId = AgentId.From("agent"),
            ConversationId = ConversationId.From("conversation"),
            SessionId = SessionId.From("session"),
            Origin = new ConversationEventOrigin(originId, CorrelationId: "request"),
            Bindings = ImmutableArray.Create(
                Binding(originId, "signalr", null, "origin-address", BindingMode.Interactive),
                Binding(observerId, "signalr", null, "observer-address", BindingMode.NotifyOnly),
                Binding(BindingId.From("muted"), "signalr", null, "muted-address", BindingMode.Muted),
                Binding(BindingId.From("other-adapter"), "signalr", "mobile", "other-adapter-address", BindingMode.Interactive),
                Binding(BindingId.From("other-channel"), "telegram", null, "other-channel-address", BindingMode.Interactive)),
            StreamEvent = new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello" }
        };

        var targets = ConversationEventStreamRouting.GetTargets(
            streamEvent, ChannelKey.From("signalr"), adapterId: null);

        targets.Length.ShouldBe(2);
        targets[0].BindingId.ShouldBe(originId);
        targets[0].ChannelRequestId.ShouldBe("request");
        targets[1].BindingId.ShouldBe(observerId);
        targets[1].ChannelRequestId.ShouldBeNull();
    }

    [Fact]
    public void GetTargets_IgnoresNonAgentEventsAndEventsWithoutSession()
    {
        var lifecycleEvent = new ConversationCreatedEvent
        {
            AgentId = AgentId.From("agent"),
            ConversationId = ConversationId.From("conversation")
        };
        var sessionlessAgentEvent = new ConversationAgentEvent
        {
            AgentId = AgentId.From("agent"),
            ConversationId = ConversationId.From("conversation"),
            StreamEvent = new AgentStreamEvent { Type = AgentStreamEventType.MessageStart }
        };

        ConversationEventStreamRouting.GetTargets(lifecycleEvent, ChannelKey.From("signalr"), null).ShouldBeEmpty();
        ConversationEventStreamRouting.GetTargets(sessionlessAgentEvent, ChannelKey.From("signalr"), null).ShouldBeEmpty();
    }

    private static ConversationBindingSnapshot Binding(
        BindingId id,
        string channelType,
        string? adapterId,
        string address,
        BindingMode mode)
        => new(id, ChannelKey.From(channelType), adapterId, ChannelAddress.From(address), mode, ThreadingMode.Single);
}
