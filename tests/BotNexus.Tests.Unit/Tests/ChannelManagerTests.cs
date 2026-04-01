using BotNexus.Channels.Base;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class ChannelManagerTests
{
    private sealed class TestChannel : IChannel
    {
        public string Name { get; }
        public string DisplayName { get; }
        public bool IsRunning { get; private set; }
        public bool SupportsStreaming => false;
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }

        public TestChannel(string name)
        {
            Name = name;
            DisplayName = name;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            StartCalled = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            StopCalled = true;
            return Task.CompletedTask;
        }

        public Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SendDeltaAsync(string chatId, string delta, IReadOnlyDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsAllowed(string senderId) => true;
    }

    [Fact]
    public async Task StartAllAsync_StartsAllChannels()
    {
        var channels = new[] { new TestChannel("ch1"), new TestChannel("ch2") };
        var manager = new ChannelManager(channels, NullLogger<ChannelManager>.Instance);

        await manager.StartAllAsync();

        channels.Should().AllSatisfy(c => c.IsRunning.Should().BeTrue());
        channels.Should().AllSatisfy(c => c.StartCalled.Should().BeTrue());
    }

    [Fact]
    public async Task StopAllAsync_StopsRunningChannels()
    {
        var channels = new[] { new TestChannel("ch1"), new TestChannel("ch2") };
        var manager = new ChannelManager(channels, NullLogger<ChannelManager>.Instance);

        await manager.StartAllAsync();
        await manager.StopAllAsync();

        channels.Should().AllSatisfy(c => c.IsRunning.Should().BeFalse());
        channels.Should().AllSatisfy(c => c.StopCalled.Should().BeTrue());
    }

    [Fact]
    public async Task StopAllAsync_SkipsNonRunningChannels()
    {
        var running = new TestChannel("running");
        var stopped = new TestChannel("stopped");
        var manager = new ChannelManager([running, stopped], NullLogger<ChannelManager>.Instance);

        await running.StartAsync();

        await manager.StopAllAsync();

        running.StopCalled.Should().BeTrue();
        stopped.StopCalled.Should().BeFalse();
    }

    [Fact]
    public void GetChannel_ByName_ReturnsChannel()
    {
        var channels = new[] { new TestChannel("telegram"), new TestChannel("discord") };
        var manager = new ChannelManager(channels, NullLogger<ChannelManager>.Instance);

        var result = manager.GetChannel("telegram");

        result.Should().NotBeNull();
        result!.Name.Should().Be("telegram");
    }

    [Fact]
    public void GetChannel_CaseInsensitive()
    {
        var channels = new[] { new TestChannel("Telegram") };
        var manager = new ChannelManager(channels, NullLogger<ChannelManager>.Instance);

        manager.GetChannel("telegram").Should().NotBeNull();
        manager.GetChannel("TELEGRAM").Should().NotBeNull();
    }

    [Fact]
    public void GetChannel_NotFound_ReturnsNull()
    {
        var manager = new ChannelManager([], NullLogger<ChannelManager>.Instance);
        manager.GetChannel("nonexistent").Should().BeNull();
    }
}
