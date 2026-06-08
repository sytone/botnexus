using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace BotNexus.Extensions.Channels.Test.Tests;

public class TestChannelAdapterTests
{
    private readonly TestChannelAdapter _adapter;
    private readonly IChannelDispatcher _dispatcher;

    public TestChannelAdapterTests()
    {
        _adapter = new TestChannelAdapter(NullLogger<TestChannelAdapter>.Instance);
        _dispatcher = Substitute.For<IChannelDispatcher>();
    }

    [Fact]
    public async Task StartAsync_SetsIsRunning_ToTrue()
    {
        _adapter.IsRunning.ShouldBeFalse();

        await _adapter.StartAsync(_dispatcher);

        _adapter.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task StopAsync_SetsIsRunning_ToFalse()
    {
        await _adapter.StartAsync(_dispatcher);
        _adapter.IsRunning.ShouldBeTrue();

        await _adapter.StopAsync();

        _adapter.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task InjectMessageAsync_DispatchesToGateway()
    {
        await _adapter.StartAsync(_dispatcher);

        var message = CreateInboundMessage("Hello from test");
        await _adapter.InjectMessageAsync(message);

        await _dispatcher.Received(1).DispatchAsync(message, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SendAsync_EnqueuesMessage()
    {
        await _adapter.StartAsync(_dispatcher);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("test-conversation-1"),
            Content = "Reply from agent"
        };

        await _adapter.SendAsync(outbound);

        _adapter.DeliveredMessages.Count.ShouldBe(1);
        _adapter.DeliveredMessages.TryDequeue(out var captured).ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured.Content.ShouldBe("Reply from agent");
    }

    [Fact]
    public async Task SendStreamDeltaAsync_EnqueuesDelta()
    {
        await _adapter.StartAsync(_dispatcher);

        var target = new ChannelStreamTarget(
            ConversationId.From("conv-1"),
            SessionId.From("session-1"),
            ChannelAddress.From("test-addr-1"));

        await _adapter.SendStreamDeltaAsync(target, "Hello ");
        await _adapter.SendStreamDeltaAsync(target, "world!");

        _adapter.DeliveredDeltas.Count.ShouldBe(2);

        _adapter.DeliveredDeltas.TryDequeue(out var first).ShouldBeTrue();
        first.Target.ShouldBe(target);
        first.Delta.ShouldBe("Hello ");

        _adapter.DeliveredDeltas.TryDequeue(out var second).ShouldBeTrue();
        second.Target.ShouldBe(target);
        second.Delta.ShouldBe("world!");
    }

    [Fact]
    public async Task ClearDelivered_EmptiesQueues()
    {
        await _adapter.StartAsync(_dispatcher);

        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("test-conversation-1"),
            Content = "Message 1"
        };
        await _adapter.SendAsync(outbound);

        var target = new ChannelStreamTarget(
            ConversationId.From("conv-1"),
            SessionId.From("session-1"),
            ChannelAddress.From("test-addr-1"));
        await _adapter.SendStreamDeltaAsync(target, "delta");

        _adapter.DeliveredMessages.Count.ShouldBe(1);
        _adapter.DeliveredDeltas.Count.ShouldBe(1);

        _adapter.ClearDelivered();

        _adapter.DeliveredMessages.Count.ShouldBe(0);
        _adapter.DeliveredDeltas.Count.ShouldBe(0);
    }

    [Fact]
    public async Task InjectMessageAsync_BeforeStart_ThrowsInvalidOperation()
    {
        var message = CreateInboundMessage("Should fail");

        await Should.ThrowAsync<InvalidOperationException>(
            () => _adapter.InjectMessageAsync(message));
    }

    private static InboundMessage CreateInboundMessage(string content) => new()
    {
        ChannelType = ChannelKey.From("test"),
        SenderId = "test-user-1",
        Sender = CitizenId.Of(UserId.From("test-user-1")),
        ChannelAddress = ChannelAddress.From("test-conversation-1"),
        Content = content
    };
}
