using BotNexus.Gateway.Channels;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Tests;

public sealed class ChannelManagerTests
{
    [Fact]
    public void Get_WithCaseInsensitiveChannelType_ReturnsAdapter()
    {
        var adapter = new TestChannelAdapter("SignalR");
        var manager = new ChannelManager([adapter]);

        var resolved = manager.Get(ChannelKey.From("signalr"));

        resolved.ShouldBeSameAs(adapter);
    }

    [Fact]
    public void Get_WhenNoAdaptersRegistered_ReturnsNull()
    {
        var manager = new ChannelManager([]);

        var resolved = manager.Get(ChannelKey.From("signalr"));

        resolved.ShouldBeNull();
    }

    [Fact]
    public void Adapters_WithEmptyList_ReturnsEmptyCollection()
    {
        var manager = new ChannelManager([]);

        manager.Adapters.ShouldBeEmpty();
    }

    // --- Multi-adapter same type tests (issue #127) ---

    [Fact]
    public void GetByAdapterId_WithMatchingAdapterId_ReturnsCorrectAdapter()
    {
        var adapter1 = new TestChannelAdapter("telegram", adapterId: "bot1");
        var adapter2 = new TestChannelAdapter("telegram", adapterId: "bot2");
        var manager = new ChannelManager([adapter1, adapter2]);

        var resolved = manager.Get(ChannelKey.From("telegram"), "bot2");

        resolved.ShouldBeSameAs(adapter2);
    }

    [Fact]
    public void GetByAdapterId_WhenAdapterIdNull_FallsBackToFirstOfType()
    {
        var adapter1 = new TestChannelAdapter("telegram", adapterId: "bot1");
        var adapter2 = new TestChannelAdapter("telegram", adapterId: "bot2");
        var manager = new ChannelManager([adapter1, adapter2]);

        var resolved = manager.Get(ChannelKey.From("telegram"), adapterId: null);

        resolved.ShouldBeSameAs(adapter1);
    }

    [Fact]
    public void GetByAdapterId_WhenAdapterIdNotFound_FallsBackToFirstOfType()
    {
        var adapter1 = new TestChannelAdapter("telegram", adapterId: "bot1");
        var adapter2 = new TestChannelAdapter("telegram", adapterId: "bot2");
        var manager = new ChannelManager([adapter1, adapter2]);

        var resolved = manager.Get(ChannelKey.From("telegram"), adapterId: "bot-unknown");

        resolved.ShouldBeSameAs(adapter1);
    }

    [Fact]
    public void GetByAdapterId_WhenNoAdaptersOfType_ReturnsNull()
    {
        var adapter1 = new TestChannelAdapter("signalr", adapterId: "hub1");
        var manager = new ChannelManager([adapter1]);

        var resolved = manager.Get(ChannelKey.From("telegram"), adapterId: "bot1");

        resolved.ShouldBeNull();
    }

    [Fact]
    public void GetByAdapterId_WithSingleAdapter_NullAdapterId_ReturnsIt()
    {
        var adapter = new TestChannelAdapter("telegram");
        var manager = new ChannelManager([adapter]);

        var resolved = manager.Get(ChannelKey.From("telegram"), adapterId: null);

        resolved.ShouldBeSameAs(adapter);
    }

    private sealed class TestChannelAdapter : IChannelAdapter
    {
        public TestChannelAdapter(string channelType, string? adapterId = null)
        {
            ChannelType = ChannelKey.From(channelType);
            DisplayName = channelType;
            AdapterId = adapterId;
        }

        public ChannelKey ChannelType { get; }
        public string DisplayName { get; }
        public string? AdapterId { get; }
        public bool SupportsStreaming => false;
        public bool SupportsSteering => false;
        public bool SupportsFollowUp => false;
        public bool SupportsThinkingDisplay => false;
        public bool SupportsToolDisplay => false;
        public bool SupportsInboundImages => false;
        public bool IsRunning => true;

        public Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
