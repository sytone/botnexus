using BotNexus.Channels.Core;
using BotNexus.Channels.Telegram;
using BotNexus.Channels.Tui;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Channels.SignalR;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net.Http;

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

        adapter.SupportsSteering.Should().BeTrue();
        adapter.SupportsFollowUp.Should().BeFalse();
        adapter.SupportsThinkingDisplay.Should().BeTrue();
        adapter.SupportsToolDisplay.Should().BeTrue();
        adapter.Should().BeAssignableTo<IStreamEventChannelAdapter>();
    }

    [Fact]
    public void SignalRAdapter_SupportsFullInteractiveCapabilities()
    {
        var adapter = new SignalRChannelAdapter(
            NullLogger<SignalRChannelAdapter>.Instance,
            Mock.Of<IHubContext<GatewayHub, IGatewayHubClient>>());

        adapter.SupportsStreaming.Should().BeTrue();
        adapter.SupportsSteering.Should().BeTrue();
        adapter.SupportsFollowUp.Should().BeTrue();
        adapter.SupportsThinkingDisplay.Should().BeTrue();
        adapter.SupportsToolDisplay.Should().BeTrue();
        adapter.Should().BeAssignableTo<IStreamEventChannelAdapter>();
    }

    [Fact]
    public void TelegramAdapter_SupportsStreamingThinkingAndToolDisplay()
    {
        using var httpClient = new HttpClient();
        var options = Options.Create(new TelegramOptions { BotToken = "token" });
        var apiClient = new TelegramBotApiClient(
            httpClient,
            options,
            NullLogger<TelegramBotApiClient>.Instance);
        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            options,
            apiClient);

        adapter.SupportsStreaming.Should().BeTrue();
        adapter.SupportsSteering.Should().BeFalse();
        adapter.SupportsFollowUp.Should().BeFalse();
        adapter.SupportsThinkingDisplay.Should().BeTrue();
        adapter.SupportsToolDisplay.Should().BeTrue();
        adapter.Should().BeAssignableTo<IStreamEventChannelAdapter>();
    }

    [Fact]
    public void ChannelCapabilities_AreReadable_ViaInterface()
    {
        IChannelAdapter adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);

        adapter.SupportsSteering.Should().BeTrue();
        adapter.SupportsFollowUp.Should().BeFalse();
        adapter.SupportsThinkingDisplay.Should().BeTrue();
        adapter.SupportsToolDisplay.Should().BeTrue();
    }

    private sealed class TestChannelAdapter : ChannelAdapterBase
    {
        public TestChannelAdapter() : base(NullLogger<TestChannelAdapter>.Instance)
        {
        }

        public override ChannelKey ChannelType => ChannelKey.From("test");
        public override string DisplayName => "Test";

        public override Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        protected override Task OnStartAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        protected override Task OnStopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
