using BotNexus.Gateway.Channels;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Extensions.Channels.Tui;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Extensions.Channels.SignalR;
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

        adapter.SupportsSteering.ShouldBeFalse();
        adapter.SupportsFollowUp.ShouldBeFalse();
        adapter.SupportsThinkingDisplay.ShouldBeFalse();
        adapter.SupportsToolDisplay.ShouldBeFalse();
    }

    [Fact]
    public void TuiAdapter_SupportsThinkingAndToolDisplay()
    {
        var adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);

        adapter.SupportsSteering.ShouldBeTrue();
        adapter.SupportsFollowUp.ShouldBeFalse();
        adapter.SupportsThinkingDisplay.ShouldBeTrue();
        adapter.SupportsToolDisplay.ShouldBeTrue();
        adapter.ShouldBeAssignableTo<IStreamEventChannelAdapter>();
    }

    [Fact]
    public void SignalRAdapter_SupportsFullInteractiveCapabilities()
    {
        var adapter = new SignalRChannelAdapter(
            NullLogger<SignalRChannelAdapter>.Instance,
            Mock.Of<IHubContext<GatewayHub, IGatewayHubClient>>());

        adapter.SupportsStreaming.ShouldBeTrue();
        adapter.SupportsSteering.ShouldBeTrue();
        adapter.SupportsFollowUp.ShouldBeTrue();
        adapter.SupportsThinkingDisplay.ShouldBeTrue();
        adapter.SupportsToolDisplay.ShouldBeTrue();
        adapter.ShouldBeAssignableTo<IStreamEventChannelAdapter>();
    }

    [Fact]
    public void TelegramAdapter_SupportsStreamingThinkingAndToolDisplay()
    {
        var options = Options.Create(new TelegramGatewayOptions { BotToken = "token" });
        var factory = new StubHttpClientFactory(_ => new HttpClient());
        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            options,
            factory);

        adapter.SupportsStreaming.ShouldBeTrue();
        adapter.SupportsSteering.ShouldBeFalse();
        adapter.SupportsFollowUp.ShouldBeFalse();
        adapter.SupportsThinkingDisplay.ShouldBeTrue();
        adapter.SupportsToolDisplay.ShouldBeTrue();
        adapter.ShouldBeAssignableTo<IStreamEventChannelAdapter>();
    }

    [Fact]
    public void ChannelCapabilities_AreReadable_ViaInterface()
    {
        IChannelAdapter adapter = new TuiChannelAdapter(NullLogger<TuiChannelAdapter>.Instance);

        adapter.SupportsSteering.ShouldBeTrue();
        adapter.SupportsFollowUp.ShouldBeFalse();
        adapter.SupportsThinkingDisplay.ShouldBeTrue();
        adapter.SupportsToolDisplay.ShouldBeTrue();
    }

    private sealed class StubHttpClientFactory(Func<string, HttpClient> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => factory(name);
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
