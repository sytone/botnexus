using BotNexus.Channels.Tui;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class TuiChannelAdapterTests
{
    [Fact]
    public void SupportsSteering_IsEnabled()
    {
        var adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);

        adapter.SupportsSteering.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_WithSteerCommand_DispatchesSteerControlMessage()
    {
        var adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);
        var dispatcher = new Mock<IChannelDispatcher>();

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var output = new StringWriter();

        try
        {
            Console.SetIn(new StringReader("/steer adjust please" + Environment.NewLine + "/quit" + Environment.NewLine));
            Console.SetOut(output);

            await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
            await Task.Delay(200);
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

        steerDispatchCount.Should().Be(1);
        output.ToString().Should().Contain("Steering queued");
    }

    [Fact]
    public async Task StartAsync_WithRegularMessage_DispatchesWithoutSteerMetadata()
    {
        var adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);
        var dispatcher = new Mock<IChannelDispatcher>();

        var originalIn = Console.In;
        var originalOut = Console.Out;
        var output = new StringWriter();

        try
        {
            Console.SetIn(new StringReader("hello world" + Environment.NewLine + "/quit" + Environment.NewLine));
            Console.SetOut(output);

            await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
            await Task.Delay(200);
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

        dispatchedMessages.Should().ContainSingle(m => m.Content == "hello world");
        dispatchedMessages.Should().OnlyContain(m => !m.Metadata.ContainsKey("control"));
        output.ToString().Should().NotContain("Steering queued");
    }
}
