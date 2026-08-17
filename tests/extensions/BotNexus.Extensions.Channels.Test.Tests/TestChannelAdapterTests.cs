using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Extensions.Channels.Test.Tests;

/// <summary>
/// Behavioural tests for <see cref="TestChannelAdapter"/> — the adapter's own contract, not the
/// gateway's. These cover the properties an integration test relies on: that an injection really
/// reaches the dispatcher, that a non-running adapter says so instead of silently dropping the
/// message, and that captured deliveries are attributable to an address and ordered.
/// </summary>
public sealed class TestChannelAdapterTests
{
    private static TestChannelAdapter CreateAdapter(TestChannelOptions? options = null)
        => new(NullLogger<TestChannelAdapter>.Instance, Options.Create(options ?? new TestChannelOptions()));

    private sealed class RecordingDispatcher : IChannelDispatcher
    {
        public List<InboundMessage> Dispatched { get; } = [];

        public Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default)
        {
            Dispatched.Add(message);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void ChannelType_UsesTheConfiguredChannelId()
    {
        // The whole point of the extension: one instance can stand in for ANY named channel, so a
        // multi-channel scenario can be exercised without a live second transport.
        var adapter = CreateAdapter(new TestChannelOptions { ChannelId = "telegram" });

        adapter.ChannelType.Value.ShouldBe("telegram");
    }

    [Fact]
    public void ChannelType_DefaultsToTest_WhenNotConfigured()
    {
        CreateAdapter().ChannelType.Value.ShouldBe("test");
    }

    [Fact]
    public async Task InjectInboundAsync_DispatchesThroughTheRealChannelDispatcher()
    {
        var adapter = CreateAdapter(new TestChannelOptions { ChannelId = "telegram" });
        var dispatcher = new RecordingDispatcher();
        await adapter.StartAsync(dispatcher);

        var accepted = await adapter.InjectInboundAsync(
            address: "chat-100",
            content: "hello from the test channel",
            senderId: "user-7",
            targetAgentId: "probe");

        accepted.ShouldBeTrue();
        var message = dispatcher.Dispatched.ShouldHaveSingleItem();
        message.ChannelType.Value.ShouldBe("telegram");
        message.ChannelAddress.Value.ShouldBe("chat-100");
        message.Content.ShouldBe("hello from the test channel");
        message.SenderId.ShouldBe("user-7");
        message.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("probe");
    }

    [Fact]
    public async Task InjectInboundAsync_ReturnsFalseAndDispatchesNothing_WhenTheAdapterIsNotStarted()
    {
        // A silently-dropped injection surfaces later as an unexplained wait timeout in whatever
        // test used it. Reporting the real reason at the point of failure is the whole value here.
        var adapter = CreateAdapter();

        var accepted = await adapter.InjectInboundAsync("chat-1", "hello");

        accepted.ShouldBeFalse();
    }

    [Fact]
    public async Task SendAsync_CapturesTheDeliveryAgainstItsChannelAddress()
    {
        var adapter = CreateAdapter();
        await adapter.StartAsync(new RecordingDispatcher());

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("chan-A"),
            Content = "User Said: hello",
            SessionId = "s-1",
            ConversationId = "c_1",
        });

        var record = adapter.GetOutbound("chan-A").ShouldHaveSingleItem();
        record.Content.ShouldBe("User Said: hello");
        record.SessionId.ShouldBe("s-1");
        record.ConversationId.ShouldBe("c_1");
        record.IsStreamDelta.ShouldBeFalse();

        // Attribution is the load-bearing property: a delivery to one address must never be
        // visible on another, or a cross-channel assertion would pass on the wrong message.
        adapter.GetOutbound("chan-B").ShouldBeEmpty();
    }

    [Fact]
    public async Task SendStreamDeltaAsync_CapturesDeltasDistinguishablyFromCompleteMessages()
    {
        var adapter = CreateAdapter();
        await adapter.StartAsync(new RecordingDispatcher());

        await adapter.SendStreamDeltaAsync(
            new ChannelStreamTarget(
                ConversationId.From("c_1"),
                SessionId.From("s-1"),
                ChannelAddress.From("chan-A")),
            "partial ");

        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("chan-A"),
            Content = "partial and complete",
        });

        var records = adapter.GetOutbound("chan-A");
        records.Count.ShouldBe(2);
        records[0].IsStreamDelta.ShouldBeTrue();
        records[1].IsStreamDelta.ShouldBeFalse();

        // Without the flag a test asserting on "the reply" would match the first delta and see
        // truncated text, which reads as a content bug rather than a harness one.
        records.Count(record => !record.IsStreamDelta).ShouldBe(1);
    }

    [Fact]
    public async Task GetAllOutbound_OrdersAcrossAddressesByCaptureSequence()
    {
        var adapter = CreateAdapter();
        await adapter.StartAsync(new RecordingDispatcher());

        await adapter.SendAsync(Message("chan-A", "first"));
        await adapter.SendAsync(Message("chan-B", "second"));
        await adapter.SendAsync(Message("chan-A", "third"));

        adapter.GetAllOutbound().Select(record => record.Content)
            .ShouldBe(["first", "second", "third"]);
    }

    [Fact]
    public async Task ClearOutbound_RemovesOnlyTheNamedAddress()
    {
        var adapter = CreateAdapter();
        await adapter.StartAsync(new RecordingDispatcher());
        await adapter.SendAsync(Message("chan-A", "a"));
        await adapter.SendAsync(Message("chan-B", "b"));

        var cleared = adapter.ClearOutbound("chan-A");

        cleared.ShouldBe(1);
        adapter.GetOutbound("chan-A").ShouldBeEmpty();
        adapter.GetOutbound("chan-B").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task StopAsync_DiscardsCapturedDeliveries()
    {
        var adapter = CreateAdapter();
        await adapter.StartAsync(new RecordingDispatcher());
        await adapter.SendAsync(Message("chan-A", "a"));

        await adapter.StopAsync();

        adapter.IsRunning.ShouldBeFalse();
        adapter.GetAllOutbound().ShouldBeEmpty();
    }

    private static OutboundMessage Message(string address, string content) => new()
    {
        ChannelType = ChannelKey.From("test"),
        ChannelAddress = ChannelAddress.From(address),
        Content = content,
    };
}
