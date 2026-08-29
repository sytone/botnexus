using System.Text;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.ServiceBus.Tests.Fakes;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Extensions.Channels.ServiceBus.Tests;

/// <summary>
/// #3518: the outbound precedence chain must not adopt an INTERNAL <c>c_&lt;guid&gt;</c> conversation
/// id as if it were a destination.
/// <para>
/// PR #3418 began stamping the gateway's internal conversation id onto every fan-out envelope.
/// Precedence rule 1 ("the producing session's own ConversationId always wins") took it verbatim,
/// short-circuiting rules 2-4 — so the genuine external address sitting in the pending reply
/// context was never consulted and the #2815 validity clause refused the send. 31 refused envelopes
/// in a 24h window.
/// </para>
/// <para>
/// The remedy is a NARROWING of rule 1, never a widening of the guard: relaxing
/// <c>TryDescribeNonExternalDestination</c> would reintroduce the #2529 cross-conversation
/// misdelivery. The last test here is the fence that proves the guard still refuses when no
/// external address exists anywhere.
/// </para>
/// </summary>
public sealed class ServiceBusOutboundInternalConversationIdTests
{
    private const string InboundQueue = "test-inbound";
    private const string ReplyQueue = "test-outbound";
    private const string InternalConversationId = "c_e605a3784017447da1c26000cf32edce";
    private const string ExternalConversation = "19:meeting_abcd1234@thread.v2";

    private static ServiceBusChannelOptions Options() => new()
    {
        ConnectionString = "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=FAKE=",
        InboundQueueName = InboundQueue,
        DefaultReplyQueueName = ReplyQueue,
    };

    private static ServiceBusChannelAdapter CreateStartedAdapter(FakeServiceBusAdapterClientFactory factory)
    {
        var adapter = new ServiceBusChannelAdapter(
            new CapturingLogger<ServiceBusChannelAdapter>(),
            new OptionsWrapper<ServiceBusChannelOptions>(Options()),
            factory);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        adapter.StartAsync(dispatcher.Object).GetAwaiter().GetResult();
        return adapter;
    }

    private static string SentBody(FakeServiceBusAdapterClientFactory factory)
    {
        var sender = factory.Senders[ReplyQueue];
        var message = sender.SentMessages.ShouldHaveSingleItem();
        return Encoding.UTF8.GetString(message.Body.ToArray());
    }

    /// <summary>
    /// The defect, end to end: an inbound external request registers a pending reply context, and
    /// the gateway fan-out then hands back an envelope stamped with the INTERNAL conversation id.
    /// The reply must be emitted carrying the EXTERNAL address recovered from that context.
    /// <para>
    /// Non-vacuity: restore rule 1 to "any non-empty own ConversationId wins" and this reddens with
    /// an <see cref="InvalidOperationException"/> naming the internal id — exactly the production
    /// error line from #3518.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SendAsync_InternalConversationIdWithPendingContext_EmitsExternalAddress()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateStartedAdapter(factory);

        await adapter.HandleMessageBodyAsync(
            $$"""
              { "content": "ping", "senderId": "user@example.com",
                "conversationId": "{{ExternalConversation}}" }
              """,
            null,
            "msg-3518",
            CancellationToken.None);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From(ExternalConversation),
            Content = "pong",
            // Exactly what OutboundResponseDeliverer stamps since PR #3418.
            ConversationId = InternalConversationId,
            ChannelRequestId = "msg-3518",
        };

        await adapter.SendAsync(outbound, CancellationToken.None);

        var body = SentBody(factory);
        body.ShouldContain(ExternalConversation);
        body.ShouldNotContain(InternalConversationId,
            customMessage: "the internal conversation id must never reach the wire as a destination");

        await adapter.StopAsync();
    }

    /// <summary>
    /// With no pending context at all, rule 4 (the channel address) supplies the destination. This
    /// is the fan-out shape where the binding address IS the external conversation.
    /// </summary>
    [Fact]
    public async Task SendAsync_InternalConversationIdNoContext_FallsBackToExternalChannelAddress()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateStartedAdapter(factory);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From(ExternalConversation),
            Content = "pong",
            ConversationId = InternalConversationId,
        };

        await adapter.SendAsync(outbound, CancellationToken.None);

        var body = SentBody(factory);
        body.ShouldContain(ExternalConversation);
        body.ShouldNotContain(InternalConversationId);

        await adapter.StopAsync();
    }

    /// <summary>
    /// An EXTERNAL own-ConversationId still wins rule 1 outright. Without this the narrowing could
    /// be satisfied by simply ignoring the field, which would regress #2529's precedence.
    /// </summary>
    [Fact]
    public async Task SendAsync_ExternalConversationId_StillWinsPrecedence()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateStartedAdapter(factory);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("some-other-address"),
            Content = "pong",
            ConversationId = ExternalConversation,
        };

        await adapter.SendAsync(outbound, CancellationToken.None);

        SentBody(factory).ShouldContain(ExternalConversation);

        await adapter.StopAsync();
    }

    /// <summary>
    /// THE FENCE (#2815 / #2529): when the internal id is the ONLY candidate — the channel address
    /// is an agent id and no pending context exists — the guard must still refuse. This clause is
    /// what makes the narrowing above safe; if a future change "fixes" the refusal by widening
    /// <c>TryDescribeNonExternalDestination</c>, this test reddens.
    /// </summary>
    [Fact]
    public async Task SendAsync_InternalIdAndAgentIdAddressOnly_StillRefuses()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateStartedAdapter(factory);

        // Register 'keel' as a known agent id via an inbound envelope, mirroring production.
        await adapter.HandleMessageBodyAsync(
            """{ "content": "ping", "senderId": "user@example.com", "agentId": "keel", "conversationId": "19:other@thread.v2" }""",
            null,
            "msg-agent",
            CancellationToken.None);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("servicebus"),
            ChannelAddress = ChannelAddress.From("keel"),
            Content = "pong",
            ConversationId = InternalConversationId,
            ChannelRequestId = "no-such-request",
        };

        await Should.ThrowAsync<InvalidOperationException>(
            () => adapter.SendAsync(outbound, CancellationToken.None));

        await adapter.StopAsync();
    }

    /// <summary>
    /// The deliverer-side probe (<see cref="IAddressableChannelAdapter"/>): the adapter reports an
    /// agent-id address with no pending request as undeliverable, so the fan-out can skip it rather
    /// than build an envelope that is certain to be refused.
    /// </summary>
    [Fact]
    public async Task CanDeliverTo_AgentIdAddressWithNoPendingRequest_ReportsUndeliverable()
    {
        var factory = new FakeServiceBusAdapterClientFactory();
        var adapter = CreateStartedAdapter(factory);

        await adapter.HandleMessageBodyAsync(
            """{ "content": "ping", "senderId": "user@example.com", "agentId": "keel", "conversationId": "19:other@thread.v2" }""",
            null,
            "msg-known-agent",
            CancellationToken.None);

        var probe = (IAddressableChannelAdapter)adapter;

        probe.CanDeliverTo(ChannelAddress.From("keel"), out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull();
        reason.ShouldContain("keel");

        // An external address, and an internal-looking one that HAS a pending request, stay deliverable.
        probe.CanDeliverTo(ChannelAddress.From(ExternalConversation), out _).ShouldBeTrue();
        probe.CanDeliverTo(ChannelAddress.From("19:other@thread.v2"), out _).ShouldBeTrue();

        await adapter.StopAsync();
    }
}
