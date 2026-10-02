using System.Collections.Immutable;
using BotNexus.Domain.Gateway.Models;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Tui;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Events;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class TuiChannelAdapterTests
{
    [Fact]
    public void SupportsSteering_IsEnabled()
    {
        var adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);

        adapter.SupportsSteering.ShouldBeTrue();
    }

    [Fact]
    public async Task StartAsync_WithSteerCommand_DispatchesSteerControlMessage()
    {
        var output = new StringWriter();
        var adapter = CreateAdapter(
            "/steer adjust please" + Environment.NewLine + "/quit" + Environment.NewLine,
            output);
        var dispatchedTcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((msg, _) => dispatchedTcs.TrySetResult(msg))
            .Returns(Task.CompletedTask);

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await dispatchedTcs.Task.WaitAsync(cts.Token);
        await adapter.StopAsync(CancellationToken.None);

        var dispatchedMessages = dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => i.Arguments[0])
            .OfType<InboundMessage>()
            .ToList();

        var steerDispatchCount = dispatchedMessages.Count(m =>
        {
            if (m.Content != "adjust please")
                return false;

            if (!m.Metadata.TryGetValue("control", out var value))
                return false;

            return string.Equals(value?.ToString(), "steer", StringComparison.OrdinalIgnoreCase);
        });

        steerDispatchCount.ShouldBe(1);
        output.ToString().ShouldContain("Steering queued");

        // PR2 of W-5 (#691): TUI must NOT fabricate a session id; the binding system
        // resolves (channelType=tui, channelAddress=console) to the correct conversation
        // and session naturally. A hardcoded RequestedSessionId here would shadow the
        // P9 binding resolution path and bypass the natural session lifecycle.
        var steerMessage = dispatchedMessages.Single(m => m.Content == "adjust please");
        steerMessage.RoutingHints.ShouldBeNull();
    }

    [Fact]
    public async Task StartAsync_WithRegularMessage_DispatchesWithoutSteerMetadata()
    {
        var output = new StringWriter();
        var adapter = CreateAdapter(
            "hello world" + Environment.NewLine + "/quit" + Environment.NewLine,
            output);
        var dispatchedTcs = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((msg, _) => dispatchedTcs.TrySetResult(msg))
            .Returns(Task.CompletedTask);

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await dispatchedTcs.Task.WaitAsync(cts.Token);
        await adapter.StopAsync(CancellationToken.None);

        var dispatchedMessages = dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => i.Arguments[0])
            .OfType<InboundMessage>()
            .ToList();

        dispatchedMessages.Where(m => m.Content == "hello world").ShouldHaveSingleItem();
        dispatchedMessages.ShouldAllBe(m => !m.Metadata.ContainsKey("control"));
        output.ToString().ShouldNotContain("Steering queued");

        // PR2 of W-5 (#691): TUI must NOT fabricate a session id on regular input either.
        // The conversation router will look up the (tui, console) binding for the resolved
        // agent and reuse / open a session — that's the post-P9 contract.
        var regularMessage = dispatchedMessages.Single(m => m.Content == "hello world");
        regularMessage.RoutingHints.ShouldBeNull();
    }

    [Fact]
    public async Task ConversationPublisher_ApplicableBinding_RendersContentOnce()
    {
        var output = new StringWriter();
        var adapter = CreateAdapter(string.Empty, output);
        await using var publisher = new ConversationEventPublisher([adapter]);
        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();

        (await publisher.PublishAsync(AgentEvent(
            conversationId,
            sessionId,
            AgentStreamEventType.ContentDelta,
            contentDelta: "hello",
            bindings: [Binding("tui", "console", BindingMode.Interactive)]))).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());

        output.ToString().ShouldBe("hello");
    }

    [Fact]
    public async Task ConversationPublisher_UnrelatedAndMutedBindings_RenderNothing()
    {
        var output = new StringWriter();
        var adapter = CreateAdapter(string.Empty, output);
        await using var publisher = new ConversationEventPublisher([adapter]);
        var conversationId = ConversationId.Create();
        var sessionId = SessionId.Create();

        (await publisher.PublishAsync(AgentEvent(
            conversationId,
            sessionId,
            AgentStreamEventType.ContentDelta,
            contentDelta: "unrelated",
            bindings: [Binding("telegram", "chat-1", BindingMode.Interactive)]))).ShouldBeTrue();
        (await publisher.PublishAsync(AgentEvent(
            conversationId,
            sessionId,
            AgentStreamEventType.ContentDelta,
            contentDelta: "muted",
            bindings: [Binding("tui", "console", BindingMode.Muted)]))).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());

        output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task ConversationPublisher_UnsupportedLifecycleEvent_RendersNothing()
    {
        var output = new StringWriter();
        var adapter = CreateAdapter(string.Empty, output);
        await using var publisher = new ConversationEventPublisher([adapter]);

        (await publisher.PublishAsync(CreatedEvent())).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());

        output.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task ConversationPublisher_ProcessConsoleNoise_DoesNotContaminateAdapterOutput()
    {
        var output = new StringWriter();
        var adapter = CreateAdapter(string.Empty, output);
        await using var publisher = new ConversationEventPublisher([adapter]);

        Console.Out.WriteLine("concurrent gateway startup");
        (await publisher.PublishAsync(CreatedEvent())).ShouldBeTrue();

        await publisher.WaitForDrainAsync(TestTimeout());

        output.ToString().ShouldBeEmpty();
    }

    private static TuiChannelAdapter CreateAdapter(string input, TextWriter output)
        => new(
            NullLogger<TuiChannelAdapter>.Instance,
            new StringReader(input),
            output);

    private static ConversationCreatedEvent CreatedEvent()
        => new()
        {
            AgentId = AgentId.From("farnsworth"),
            ConversationId = ConversationId.Create(),
            Bindings = [Binding("tui", "console", BindingMode.Interactive)],
            Title = "new conversation",
        };

    private static ConversationBindingSnapshot Binding(string channel, string address, BindingMode mode)
        => new(
            BindingId.Create(),
            ChannelKey.From(channel),
            AdapterId: null,
            ChannelAddress.From(address),
            mode,
            ThreadingMode.Single);

    private static ConversationAgentEvent AgentEvent(
        ConversationId conversationId,
        SessionId sessionId,
        AgentStreamEventType type,
        string? contentDelta,
        ImmutableArray<ConversationBindingSnapshot> bindings)
        => new()
        {
            AgentId = AgentId.From("farnsworth"),
            ConversationId = conversationId,
            SessionId = sessionId,
            Bindings = bindings,
            StreamEvent = new AgentStreamEvent
            {
                Type = type,
                ContentDelta = contentDelta,
                AgentId = AgentId.From("farnsworth"),
                ConversationId = conversationId,
                SessionId = sessionId,
            },
        };

    private static CancellationToken TestTimeout()
        => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;
}
