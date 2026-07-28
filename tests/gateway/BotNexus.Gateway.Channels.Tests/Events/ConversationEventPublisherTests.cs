using System.Collections.Immutable;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Channels.Tests.Events;

/// <summary>
/// Seam tests for the channel-neutral conversation event publisher (issue #2085).
/// <para>
/// Every test drives the real <see cref="ConversationEventPublisher"/> against real, small
/// sink implementations. No mocking framework is used for the publisher or the sinks: the
/// point of this seam is its concurrency, ordering, and isolation behaviour, and a recorded
/// mock expectation would assert nothing about any of that.
/// </para>
/// </summary>
public sealed class ConversationEventPublisherTests
{
    private static readonly AgentId Agent = AgentId.From("farnsworth");

    private static ConversationEventPublisher CreatePublisher(
        IEnumerable<IConversationEventSink> sinks,
        ConversationEventPublisherOptions? options = null)
        => new(sinks, options);

    private static ConversationBindingSnapshot Binding(string channel, string address)
        => new(
            BindingId.Create(),
            ChannelKey.From(channel),
            AdapterId: null,
            ChannelAddress.From(address),
            BindingMode.Interactive,
            ThreadingMode.Single);

    private static ConversationAgentEvent AgentEvent(
        ConversationId conversationId,
        SessionId sessionId,
        string delta,
        ImmutableArray<ConversationBindingSnapshot>? bindings = null)
        => new()
        {
            AgentId = Agent,
            ConversationId = conversationId,
            SessionId = sessionId,
            Origin = new ConversationEventOrigin(CorrelationId: "corr-1"),
            Bindings = bindings ?? ImmutableArray.Create(Binding("signalr", "conn-1")),
            StreamEvent = new AgentStreamEvent
            {
                Type = AgentStreamEventType.ContentDelta,
                ContentDelta = delta,
                ConversationId = conversationId,
                SessionId = sessionId,
                AgentId = Agent,
            },
        };

    /// <summary>
    /// Requirement 1: two independent real sinks both receive the very same immutable event
    /// instance, carrying strongly typed identifiers rather than stringly-typed copies.
    /// </summary>
    [Fact]
    public async Task PublishAsync_DeliversSameImmutableEventAndTypedIdentifiers_ToEverySink()
    {
        var first = new CapturingConversationEventSink();
        var second = new CapturingConversationEventSink();
        await using var publisher = CreatePublisher([first, second]);

        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();
        var published = AgentEvent(conversationId, sessionId, "hello");

        Assert.True(await publisher.PublishAsync(published));
        await publisher.WaitForDrainAsync(TestTimeout());

        var firstSeen = Assert.Single(first.Received);
        var secondSeen = Assert.Single(second.Received);

        Assert.Same(published, firstSeen);
        Assert.Same(published, secondSeen);
        Assert.Same(firstSeen, secondSeen);

        Assert.Equal(Agent, firstSeen.AgentId);
        Assert.Equal(conversationId, firstSeen.ConversationId);
        Assert.Equal(sessionId, firstSeen.SessionId);
    }

    /// <summary>
    /// Requirement 2: a sink with no interested recipient no-ops. It is still offered the
    /// event (so it can make that decision), and its inaction changes nothing for the sink
    /// that does have a recipient.
    /// </summary>
    [Fact]
    public async Task PublishAsync_SinkWithNoInterestedRecipient_NoOpsWithoutAffectingOtherSinks()
    {
        var uninterested = new NoInterestedRecipientConversationEventSink();
        var interested = new CapturingConversationEventSink();
        await using var publisher = CreatePublisher([uninterested, interested]);

        var conversationId = ConversationId.Create();
        Assert.True(await publisher.PublishAsync(AgentEvent(conversationId, SessionId.Create(), "hello")));
        await publisher.WaitForDrainAsync(TestTimeout());

        Assert.Equal(1, uninterested.OfferedCount);
        Assert.Single(interested.Received);
    }

    /// <summary>
    /// Requirement 3: a throwing sink is isolated. The failure neither propagates to the
    /// publisher's caller nor suppresses delivery to the healthy sink, in either registration
    /// order.
    /// </summary>
    [Fact]
    public async Task PublishAsync_FailingSink_DoesNotSuppressDeliveryToOtherSinks()
    {
        var failing = new ThrowingConversationEventSink();
        var healthyAfter = new CapturingConversationEventSink();
        var healthyBefore = new CapturingConversationEventSink();
        await using var publisher = CreatePublisher([healthyBefore, failing, healthyAfter]);

        var conversationId = ConversationId.Create();
        Assert.True(await publisher.PublishAsync(AgentEvent(conversationId, SessionId.Create(), "hello")));
        await publisher.WaitForDrainAsync(TestTimeout());

        Assert.Equal(1, failing.OfferedCount);
        Assert.Single(healthyBefore.Received);
        Assert.Single(healthyAfter.Received);
    }

    /// <summary>
    /// Requirement 4: events for one conversation are observed by a sink in publication order,
    /// even when interleaved with traffic for other conversations.
    /// </summary>
    [Fact]
    public async Task PublishAsync_EventsForOneConversation_AreObservedInPublicationOrder()
    {
        var sink = new CapturingConversationEventSink();
        await using var publisher = CreatePublisher([sink]);

        var target = ConversationId.Create();
        var other = ConversationId.Create();
        var sessionId = SessionId.Create();

        const int count = 200;
        for (var i = 0; i < count; i++)
        {
            Assert.True(await publisher.PublishAsync(AgentEvent(target, sessionId, $"delta-{i}")));
            Assert.True(await publisher.PublishAsync(AgentEvent(other, sessionId, "noise")));
        }

        await publisher.WaitForDrainAsync(TestTimeout());

        var observed = sink.Received
            .OfType<ConversationAgentEvent>()
            .Where(e => e.ConversationId == target)
            .Select(e => e.StreamEvent.ContentDelta)
            .ToArray();

        Assert.Equal(Enumerable.Range(0, count).Select(i => $"delta-{i}").ToArray(), observed);
    }

    /// <summary>
    /// Requirement 5: the nested agent stream event is passed through untouched - same type,
    /// same instance, same payload. Any transformation here would silently fork the wire shape
    /// between the agent loop and the channel extensions.
    /// </summary>
    [Fact]
    public async Task PublishAsync_DoesNotTransformNestedAgentStreamEvent()
    {
        var sink = new CapturingConversationEventSink();
        await using var publisher = CreatePublisher([sink]);

        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();
        var streamEvent = new AgentStreamEvent
        {
            Type = AgentStreamEventType.ToolStart,
            ToolCallId = "call-7",
            ToolName = "exec",
            ToolArgs = new Dictionary<string, object?> { ["command"] = "ls" },
            ConversationId = conversationId,
            SessionId = sessionId,
            AgentId = Agent,
        };

        var published = new ConversationAgentEvent
        {
            AgentId = Agent,
            ConversationId = conversationId,
            SessionId = sessionId,
            StreamEvent = streamEvent,
        };

        Assert.True(await publisher.PublishAsync(published));
        await publisher.WaitForDrainAsync(TestTimeout());

        var received = Assert.IsType<ConversationAgentEvent>(Assert.Single(sink.Received));
        Assert.Same(streamEvent, received.StreamEvent);
        Assert.IsType<AgentStreamEvent>(received.StreamEvent);
        Assert.Equal(AgentStreamEventType.ToolStart, received.StreamEvent.Type);
        Assert.Equal("call-7", received.StreamEvent.ToolCallId);
        Assert.Equal("exec", received.StreamEvent.ToolName);
        Assert.Equal("ls", received.StreamEvent.ToolArgs!["command"]);
    }

    /// <summary>
    /// Requirement 6: a sink cannot mutate the binding or origin snapshot in a way another sink
    /// observes. The first sink actively attempts the mutation; the second must still see the
    /// pristine values.
    /// </summary>
    [Fact]
    public async Task PublishAsync_SinkCannotMutateBindingOrOriginSnapshot_ObservedByAnotherSink()
    {
        var mutator = new MutationAttemptingConversationEventSink();
        var observer = new CapturingConversationEventSink();
        await using var publisher = CreatePublisher([mutator, observer]);

        var conversationId = ConversationId.Create();
        var bindings = ImmutableArray.Create(Binding("signalr", "conn-1"), Binding("telegram", "chat-9"));
        var origin = new ConversationEventOrigin(bindings[0].BindingId, UserId.From("jon"), "corr-42");

        var published = new ConversationAgentEvent
        {
            AgentId = Agent,
            ConversationId = conversationId,
            SessionId = SessionId.Create(),
            Origin = origin,
            Bindings = bindings,
            StreamEvent = new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "x" },
        };

        Assert.True(await publisher.PublishAsync(published));
        await publisher.WaitForDrainAsync(TestTimeout());

        Assert.True(mutator.AttemptedMutation);

        var seen = Assert.Single(observer.Received);
        Assert.Same(origin, seen.Origin);
        Assert.Equal("corr-42", seen.Origin.CorrelationId);
        Assert.Equal(bindings[0].BindingId, seen.Origin.BindingId);
        Assert.Equal(2, seen.Bindings.Length);
        Assert.Null(seen.Bindings[0].AdapterId);
        Assert.Equal(ChannelKey.From("signalr"), seen.Bindings[0].ChannelType);
        Assert.Equal(ChannelKey.From("telegram"), seen.Bindings[1].ChannelType);
    }

    /// <summary>
    /// Backpressure: a slow sink must not block the publishing hot path. Publication returns
    /// immediately while the sink is still wedged inside its handler.
    /// </summary>
    [Fact]
    public async Task PublishAsync_SlowSink_DoesNotBlockThePublishingCaller()
    {
        var blocking = new BlockingConversationEventSink();
        await using var publisher = CreatePublisher(
            [blocking],
            new ConversationEventPublisherOptions { SinkTimeout = TimeSpan.FromSeconds(30) });

        var conversationId = ConversationId.Create();
        Assert.True(await publisher.PublishAsync(AgentEvent(conversationId, SessionId.Create(), "first")));
        await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        // The sink is still inside its handler here; publication must complete anyway.
        Assert.True(await publisher.PublishAsync(AgentEvent(conversationId, SessionId.Create(), "second")));

        blocking.Release();
        await publisher.WaitForDrainAsync(TestTimeout());
    }

    /// <summary>
    /// Backpressure: the per-conversation buffer is bounded. Once full, publication sheds the
    /// newest event by returning false rather than growing without limit behind a wedged sink.
    /// </summary>
    [Fact]
    public async Task PublishAsync_WhenPerConversationBufferIsFull_ShedsInsteadOfGrowing()
    {
        var blocking = new BlockingConversationEventSink();
        await using var publisher = CreatePublisher(
            [blocking],
            new ConversationEventPublisherOptions
            {
                PerConversationCapacity = 2,
                SinkTimeout = TimeSpan.FromSeconds(30),
            });

        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();

        Assert.True(await publisher.PublishAsync(AgentEvent(conversationId, sessionId, "wedge")));
        await blocking.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        // Capacity 2: two more are buffered, everything after that is shed.
        Assert.True(await publisher.PublishAsync(AgentEvent(conversationId, sessionId, "buffered-1")));
        Assert.True(await publisher.PublishAsync(AgentEvent(conversationId, sessionId, "buffered-2")));
        Assert.False(await publisher.PublishAsync(AgentEvent(conversationId, sessionId, "shed")));

        blocking.Release();
        await publisher.WaitForDrainAsync(TestTimeout());
    }

    /// <summary>
    /// Cancellation: a sink that overruns the configured budget has its token cancelled and is
    /// abandoned, and the conversation's pump keeps moving.
    /// </summary>
    [Fact]
    public async Task PublishAsync_SinkExceedingTimeout_IsCancelledAndPumpContinues()
    {
        var blocking = new BlockingConversationEventSink();
        var healthy = new CapturingConversationEventSink();
        await using var publisher = CreatePublisher(
            [blocking, healthy],
            new ConversationEventPublisherOptions { SinkTimeout = TimeSpan.FromMilliseconds(100) });

        var conversationId = ConversationId.Create();
        Assert.True(await publisher.PublishAsync(AgentEvent(conversationId, SessionId.Create(), "hello")));
        await publisher.WaitForDrainAsync(TestTimeout());

        Assert.True(blocking.ObservedCancellation);
        Assert.Single(healthy.Received);

        blocking.Release();
    }

    /// <summary>
    /// The seam must stay channel-agnostic: no concrete channel name may appear anywhere in the
    /// publisher's source, otherwise routing policy has leaked back into the fan-out point.
    /// </summary>
    [Fact]
    public void Publisher_ContainsNoConcreteChannelName()
    {
        var source = ReadPublisherSource();

        string[] forbidden = ["signalr", "telegram", "discord", "slack", "teams", "tui", "servicebus", "agent365"];
        foreach (var name in forbidden)
        {
            Assert.DoesNotContain(name, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ReadPublisherSource()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "BotNexus.slnx")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var path = Path.Combine(
            current!.FullName,
            "src", "gateway", "BotNexus.Gateway.Channels", "ConversationEventPublisher.cs");
        Assert.True(File.Exists(path), $"Publisher source not found at {path}");
        return File.ReadAllText(path);
    }

    private static CancellationToken TestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;
}
