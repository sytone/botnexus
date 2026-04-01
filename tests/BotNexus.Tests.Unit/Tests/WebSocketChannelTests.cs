using System.Text.Json;
using BotNexus.Core.Models;
using BotNexus.Gateway;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BotNexus.Tests.Unit.Tests;

public class WebSocketChannelTests
{
    private static WebSocketChannel CreateChannel() =>
        new(NullLogger<WebSocketChannel>.Instance);

    private static OutboundMessage Outbound(string chatId, string content) =>
        new("websocket", chatId, content);

    [Fact]
    public void Name_IsWebSocket()
    {
        var ch = CreateChannel();
        ch.Name.Should().Be("websocket");
    }

    [Fact]
    public void SupportsStreaming_IsTrue()
    {
        var ch = CreateChannel();
        ch.SupportsStreaming.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_SetsIsRunning()
    {
        var ch = CreateChannel();
        await ch.StartAsync();
        ch.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task StopAsync_ClearsIsRunning()
    {
        var ch = CreateChannel();
        await ch.StartAsync();
        await ch.StopAsync();
        ch.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void IsAllowed_AlwaysReturnsTrue()
    {
        var ch = CreateChannel();
        ch.IsAllowed("anyone").Should().BeTrue();
    }

    [Fact]
    public void AddConnection_ReturnsReader()
    {
        var ch = CreateChannel();
        var reader = ch.AddConnection("conn1");
        reader.Should().NotBeNull();
    }

    [Fact]
    public async Task SendAsync_WritesResponseJson_ToMatchingConnection()
    {
        var ch = CreateChannel();
        var reader = ch.AddConnection("conn1");

        await ch.SendAsync(Outbound("conn1", "hello"));

        reader.TryRead(out var json).Should().BeTrue();
        var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("type").GetString().Should().Be("response");
        doc.RootElement.GetProperty("content").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task SendAsync_UnknownChatId_DoesNotThrow()
    {
        var ch = CreateChannel();
        var act = async () => await ch.SendAsync(Outbound("unknown", "oops"));
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendDeltaAsync_WritesDeltaJson_ToMatchingConnection()
    {
        var ch = CreateChannel();
        var reader = ch.AddConnection("conn2");

        await ch.SendDeltaAsync("conn2", "tok");

        reader.TryRead(out var json).Should().BeTrue();
        var doc = JsonDocument.Parse(json!);
        doc.RootElement.GetProperty("type").GetString().Should().Be("delta");
        doc.RootElement.GetProperty("content").GetString().Should().Be("tok");
    }

    [Fact]
    public async Task SendDeltaAsync_UnknownChatId_DoesNotThrow()
    {
        var ch = CreateChannel();
        var act = async () => await ch.SendDeltaAsync("ghost", "x");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RemoveConnection_CompletesReader()
    {
        var ch = CreateChannel();
        var reader = ch.AddConnection("conn3");

        ch.RemoveConnection("conn3");

        // Channel should be completed — ReadAllAsync terminates without items
        var items = new List<string>();
        await foreach (var item in reader.ReadAllAsync())
            items.Add(item);

        items.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveConnection_UnknownId_DoesNotThrow()
    {
        var ch = CreateChannel();
        var act = () =>
        {
            ch.RemoveConnection("nobody");
            return Task.CompletedTask;
        };
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendAsync_MultipleConnections_RoutesToCorrectOne()
    {
        var ch = CreateChannel();
        var reader1 = ch.AddConnection("c1");
        var reader2 = ch.AddConnection("c2");

        await ch.SendAsync(Outbound("c2", "for c2"));

        reader1.TryRead(out _).Should().BeFalse();
        reader2.TryRead(out var json).Should().BeTrue();
        JsonDocument.Parse(json!).RootElement
            .GetProperty("content").GetString().Should().Be("for c2");
    }

    [Fact]
    public async Task StopAsync_CompletesAllReaders()
    {
        var ch = CreateChannel();
        await ch.StartAsync();
        var reader1 = ch.AddConnection("x1");
        var reader2 = ch.AddConnection("x2");

        await ch.StopAsync();

        var items1 = new List<string>();
        await foreach (var item in reader1.ReadAllAsync()) items1.Add(item);

        var items2 = new List<string>();
        await foreach (var item in reader2.ReadAllAsync()) items2.Add(item);

        items1.Should().BeEmpty();
        items2.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_AfterRemove_DoesNotThrow()
    {
        var ch = CreateChannel();
        ch.AddConnection("gone");
        ch.RemoveConnection("gone");

        var act = async () => await ch.SendAsync(Outbound("gone", "late"));
        await act.Should().NotThrowAsync();
    }
}
