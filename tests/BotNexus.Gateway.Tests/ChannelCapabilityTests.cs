using BotNexus.Channels.Core;
using BotNexus.Channels.Telegram;
using BotNexus.Channels.Tui;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests;

public sealed class ChannelCapabilityTests
{
    [Fact]
    public void DefaultChannelAdapter_AllCapabilitiesFalse()
    {
        var adapter = new TestChannelAdapter();

        adapter.SupportsSteering.Should().BeFalse();
        adapter.SupportsFollowUp.Should().BeFalse();
        adapter.SupportsThinkingDisplay.Should().BeFalse();
        adapter.SupportsToolDisplay.Should().BeFalse();
    }

    [Fact]
    public void TuiAdapter_SupportsThinkingAndToolDisplay()
    {
        var adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);

        adapter.SupportsSteering.Should().BeFalse();
        adapter.SupportsFollowUp.Should().BeFalse();
        adapter.SupportsThinkingDisplay.Should().BeTrue();
        adapter.SupportsToolDisplay.Should().BeTrue();
    }

    [Fact]
    public void TelegramAdapter_NoAdvancedCapabilities()
    {
        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(new TelegramOptions()));

        adapter.SupportsSteering.Should().BeFalse();
        adapter.SupportsFollowUp.Should().BeFalse();
        adapter.SupportsThinkingDisplay.Should().BeFalse();
        adapter.SupportsToolDisplay.Should().BeFalse();
    }

    [Fact]
    public void ChannelCapabilities_AreReadable_ViaInterface()
    {
        IChannelAdapter adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);

        adapter.SupportsSteering.Should().BeFalse();
        adapter.SupportsFollowUp.Should().BeFalse();
        adapter.SupportsThinkingDisplay.Should().BeTrue();
        adapter.SupportsToolDisplay.Should().BeTrue();
    }

    private sealed class TestChannelAdapter : ChannelAdapterBase
    {
        public TestChannelAdapter() : base(NullLogger<TestChannelAdapter>.Instance)
        {
        }

        public override string ChannelType => "test";
        public override string DisplayName => "Test";

        public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        protected override Task OnStartAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        protected override Task OnStopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
