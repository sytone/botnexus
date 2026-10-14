using System.Collections.Immutable;
using BotNexus.Domain.Gateway.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Agent365;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Agent365.Tests;

public sealed class Agent365ConversationEventProjectionTests
{
    [Fact]
    public async Task ConversationPublisher_ApplicableBinding_SendsConsolidatedReplyOnce()
    {
        var sender = new RecordingConnectorSender();
        var adapter = CreateAdapter(sender);
        await using var publisher = new ConversationEventPublisher([adapter]);
        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();
        var binding = Binding("agent365", Agent365ChannelAddress.Create("m365-conversation", "https://smba.example.com/amer/"), BindingMode.Interactive);

        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.ContentDelta, binding, contentDelta: "hello "))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.ContentDelta, binding, contentDelta: "world"))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(conversationId, sessionId, AgentStreamEventType.MessageEnd, binding, finalContent: "hello world"))).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());

        var sent = sender.Sent.ShouldHaveSingleItem();
        sent.ServiceUrl.ShouldBe("https://smba.example.com/amer/");
        sent.ConversationId.ShouldBe("m365-conversation");
        sent.Activity.Text.ShouldBe("hello world");
    }

    [Fact]
    public async Task ConversationPublisher_UnrelatedAndMutedBindings_SendNothing()
    {
        var sender = new RecordingConnectorSender();
        var adapter = CreateAdapter(sender);
        await using var publisher = new ConversationEventPublisher([adapter]);
        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();

        (await publisher.PublishAsync(AgentEvent(
            conversationId,
            sessionId,
            AgentStreamEventType.MessageEnd,
            Binding("telegram", ChannelAddress.From("chat-1"), BindingMode.Interactive),
            finalContent: "unrelated"))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(
            conversationId,
            sessionId,
            AgentStreamEventType.MessageEnd,
            Binding("agent365", Agent365ChannelAddress.Create("muted", null), BindingMode.Muted),
            finalContent: "muted"))).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());

        sender.Sent.ShouldBeEmpty();
    }

    [Fact]
    public async Task ConversationPublisher_UnsupportedLifecycleEvent_SendsNothing()
    {
        var sender = new RecordingConnectorSender();
        var adapter = CreateAdapter(sender);
        await using var publisher = new ConversationEventPublisher([adapter]);

        (await publisher.PublishAsync(new ConversationCreatedEvent
        {
            AgentId = AgentId.From("farnsworth"),
            ConversationId = ConversationId.Create(),
            Bindings = [Binding("agent365", Agent365ChannelAddress.Create("m365-conversation", null), BindingMode.Interactive)],
            Title = "new conversation",
        })).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());

        sender.Sent.ShouldBeEmpty();
    }

    private static Agent365ChannelAdapter CreateAdapter(IAgent365ConnectorSender sender)
        => new(
            NullLogger<Agent365ChannelAdapter>.Instance,
            Options.Create(new Agent365GatewayOptions()),
            sender);

    private static ConversationBindingSnapshot Binding(string channel, ChannelAddress address, BindingMode mode)
        => new(
            BindingId.Create(),
            ChannelKey.From(channel),
            AdapterId: null,
            address,
            mode,
            ThreadingMode.Single);

    private static ConversationAgentEvent AgentEvent(
        ConversationId conversationId,
        SessionId sessionId,
        AgentStreamEventType type,
        ConversationBindingSnapshot binding,
        string? contentDelta = null,
        string? finalContent = null)
        => new()
        {
            AgentId = AgentId.From("farnsworth"),
            ConversationId = conversationId,
            SessionId = sessionId,
            Bindings = ImmutableArray.Create(binding),
            StreamEvent = new AgentStreamEvent
            {
                Type = type,
                ContentDelta = contentDelta,
                FinalContent = finalContent,
                AgentId = AgentId.From("farnsworth"),
                ConversationId = conversationId,
                SessionId = sessionId,
            },
        };

    private static CancellationToken TestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private sealed class RecordingConnectorSender : IAgent365ConnectorSender
    {
        public List<SentReply> Sent { get; } = [];

        public Task SendReplyAsync(
            string? serviceUrl,
            string conversationId,
            Activity activity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(new SentReply(serviceUrl, conversationId, activity));
            return Task.CompletedTask;
        }
    }

    private sealed record SentReply(string? ServiceUrl, string ConversationId, Activity Activity);
}
