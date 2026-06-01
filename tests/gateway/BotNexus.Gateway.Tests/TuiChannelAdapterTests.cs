using BotNexus.Extensions.Channels.Tui;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
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
        var adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);
        var dispatchedTcs = new TaskCompletionSource<InboundMessage>();
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((msg, _) => dispatchedTcs.TrySetResult(msg))
            .Returns(Task.CompletedTask);

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var output = new StringWriter();

        try
        {
            Console.SetIn(new StringReader("/steer adjust please" + Environment.NewLine + "/quit" + Environment.NewLine));
            Console.SetOut(output);

            await adapter.StartAsync(dispatcher.Object, CancellationToken.None);

            // Wait for the dispatch to occur (up to 5 seconds), then stop.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dispatchedTcs.Task.WaitAsync(cts.Token);

            await adapter.StopAsync(CancellationToken.None);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

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
        var adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);
        var dispatchedTcs = new TaskCompletionSource<InboundMessage>();
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Callback<InboundMessage, CancellationToken>((msg, _) => dispatchedTcs.TrySetResult(msg))
            .Returns(Task.CompletedTask);

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var output = new StringWriter();

        try
        {
            Console.SetIn(new StringReader("hello world" + Environment.NewLine + "/quit" + Environment.NewLine));
            Console.SetOut(output);

            await adapter.StartAsync(dispatcher.Object, CancellationToken.None);

            // Wait for the dispatch to occur (up to 5 seconds), then stop.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await dispatchedTcs.Task.WaitAsync(cts.Token);

            await adapter.StopAsync(CancellationToken.None);
        }
        finally
        {
            Console.SetIn(originalIn);
            Console.SetOut(originalOut);
        }

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
}
