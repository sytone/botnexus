using BotNexus.Channels.Core;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using FluentAssertions;

namespace BotNexus.Gateway.Tests;

public sealed class ChannelManagerTests
{
    [Fact]
    public void Get_WithCaseInsensitiveChannelType_ReturnsAdapter()
    {
        var adapter = new TestChannelAdapter("WebSocket");
        var manager = new ChannelManager([adapter]);

        var resolved = manager.Get("websocket");

        resolved.Should().BeSameAs(adapter);
    }

    [Fact]
    public void Get_WhenNoAdaptersRegistered_ReturnsNull()
    {
        var manager = new ChannelManager([]);

        var resolved = manager.Get("websocket");

        resolved.Should().BeNull();
    }

    [Fact]
    public void Adapters_WithEmptyList_ReturnsEmptyCollection()
    {
        var manager = new ChannelManager([]);

        manager.Adapters.Should().BeEmpty();
    }

    private sealed class TestChannelAdapter : IChannelAdapter
    {
        public TestChannelAdapter(string channelType)
        {
            ChannelType = channelType;
            DisplayName = channelType;
        }

        public string ChannelType { get; }
        public string DisplayName { get; }
        public bool SupportsStreaming => false;
        public bool SupportsSteering => false;
        public bool SupportsFollowUp => false;
        public bool SupportsThinkingDisplay => false;
        public bool SupportsToolDisplay => false;
        public bool IsRunning => true;

        public Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
