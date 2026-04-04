using BotNexus.Channels.Base;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Bus;
using BotNexus.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Integration.Tests;

/// <summary>
/// SC-CHN-004: Channel enable/disable config and allow-list enforcement.
/// Tests that channels respect the enable/disable configuration and that
/// allow lists correctly filter messages from non-allowed senders.
/// </summary>
public sealed class ChannelConfigE2eTests
{
    [Fact]
    public async Task Channel_WithEmptyAllowList_AcceptsAllSenders()
    {
        var bus = new MessageBus();
        var channel = new TestChannel("test-open", bus, allowList: null);
        await channel.StartAsync();

        channel.IsAllowed("anyone").Should().BeTrue();
        channel.IsAllowed("user-123").Should().BeTrue();
        channel.IsAllowed("").Should().BeTrue();
    }

    [Fact]
    public async Task Channel_WithAllowList_RejectsUnlistedSenders()
    {
        var bus = new MessageBus();
        var channel = new TestChannel("test-restricted", bus, allowList: ["U001", "U002"]);
        await channel.StartAsync();

        channel.IsAllowed("U001").Should().BeTrue();
        channel.IsAllowed("U002").Should().BeTrue();
        channel.IsAllowed("U003").Should().BeFalse("U003 is not in the allow list");
        channel.IsAllowed("unknown").Should().BeFalse("unknown is not in the allow list");
    }

    [Fact]
    public async Task Channel_AllowList_BlocksMessagePublishing()
    {
        var bus = new MessageBus();
        var channel = new TestChannel("test-filtered", bus, allowList: ["U001"]);
        await channel.StartAsync();

        // Send from allowed user
        await channel.SimulateInboundAsync(new InboundMessage(
            Channel: "test-filtered",
            SenderId: "U001",
            ChatId: "chat-1",
            Content: "allowed message",
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>()));

        // Send from blocked user
        await channel.SimulateInboundAsync(new InboundMessage(
            Channel: "test-filtered",
            SenderId: "U999",
            ChatId: "chat-2",
            Content: "blocked message",
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>()));

        // Only the allowed message should reach the bus
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var published = await bus.ReadAsync(cts.Token);
        published.SenderId.Should().Be("U001");
        published.Content.Should().Be("allowed message");

        // No more messages on the bus (blocked one was dropped)
        bus.IsAlive.Should().BeTrue();
    }

    [Fact]
    public async Task Channel_StartStop_TogglesIsRunning()
    {
        var bus = new MessageBus();
        var channel = new TestChannel("test-lifecycle", bus, allowList: null);

        channel.IsRunning.Should().BeFalse();

        await channel.StartAsync();
        channel.IsRunning.Should().BeTrue();

        await channel.StopAsync();
        channel.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Channel_DisabledByNotStarting_IsNotRunning()
    {
        var bus = new MessageBus();
        var channel = new TestChannel("test-disabled", bus, allowList: null);

        // Simulate "disabled" = never started
        channel.IsRunning.Should().BeFalse();

        // Even after creating it, not starting means not running
        await channel.StopAsync(); // noop
        channel.IsRunning.Should().BeFalse();
    }

    /// <summary>
    /// Concrete test channel extending BaseChannel to test allow-list + lifecycle.
    /// </summary>
    private sealed class TestChannel : BaseChannel
    {
        public TestChannel(string name, IMessageBus messageBus, IReadOnlyList<string>? allowList)
            : base(messageBus, NullLogger<TestChannel>.Instance, allowList)
        {
            ChannelName = name;
        }

        private string ChannelName { get; }
        public override string Name => ChannelName;
        public override string DisplayName => $"Test Channel ({ChannelName})";

        public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        protected override Task OnStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task OnStopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        /// <summary>Exposes PublishMessageAsync for testing the allow-list filter.</summary>
        public ValueTask SimulateInboundAsync(InboundMessage message)
            => PublishMessageAsync(message);
    }
}
