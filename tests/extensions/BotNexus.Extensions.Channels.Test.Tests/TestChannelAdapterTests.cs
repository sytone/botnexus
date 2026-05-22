using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Extensions.Channels.Test;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BotNexus.Extensions.Channels.Test.Tests;

public sealed class TestChannelAdapterTests
{
    private static TestChannelAdapter CreateAdapter() =>
        new TestChannelAdapter(NullLogger<TestChannelAdapter>.Instance);

    // 1. ChannelType returns "test"
    [Fact]
    public void ChannelType_IsTest()
    {
        var adapter = CreateAdapter();
        Assert.Equal(ChannelKey.From("test"), adapter.ChannelType);
    }

    // 2. GetOutbound returns empty list when no messages sent
    [Fact]
    public void GetOutbound_ReturnsEmpty_WhenNoMessages()
    {
        var adapter = CreateAdapter();
        var result = adapter.GetOutbound("chan-1");
        Assert.Empty(result);
    }

    // 3. SendAsync enqueues message, GetOutbound returns it
    [Fact]
    public async Task SendAsync_EnqueuesMessage_GetOutboundReturnsIt()
    {
        var adapter = CreateAdapter();
        var msg = new OutboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("chan-1"),
            Content = "hello world",
            Metadata = new Dictionary<string, object?>()
        };

        await adapter.SendAsync(msg);

        var result = adapter.GetOutbound("chan-1");
        Assert.Single(result);
        Assert.Equal("hello world", result[0].Content);
    }

    // 4. ClearOutbound empties the queue
    [Fact]
    public async Task ClearOutbound_EmptiesQueue()
    {
        var adapter = CreateAdapter();
        var msg = new OutboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("chan-2"),
            Content = "test",
            Metadata = new Dictionary<string, object?>()
        };
        await adapter.SendAsync(msg);

        adapter.ClearOutbound("chan-2");

        Assert.Empty(adapter.GetOutbound("chan-2"));
    }

    // 5. GetOutbound dequeues messages (subsequent call returns empty)
    [Fact]
    public async Task GetOutbound_DequeuesMessages_SubsequentCallReturnsEmpty()
    {
        var adapter = CreateAdapter();
        var msg = new OutboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("chan-3"),
            Content = "msg",
            Metadata = new Dictionary<string, object?>()
        };
        await adapter.SendAsync(msg);

        var first = adapter.GetOutbound("chan-3");
        var second = adapter.GetOutbound("chan-3");

        Assert.Single(first);
        Assert.Empty(second);
    }

    // 6. GetLogs returns captured log entries
    [Fact]
    public void GetLogs_ReturnsCapturedEntries()
    {
        var adapter = CreateAdapter();
        adapter.CaptureLog(new TestLogEntry(DateTimeOffset.UtcNow, "Information", "Test message", []));

        var logs = adapter.GetLogs();

        Assert.Single(logs);
        Assert.Equal("Test message", logs[0].Message);
    }

    // 7. ClearLogs empties the log buffer
    [Fact]
    public void ClearLogs_EmptiesLogBuffer()
    {
        var adapter = CreateAdapter();
        adapter.CaptureLog(new TestLogEntry(DateTimeOffset.UtcNow, "Warning", "warn", []));
        adapter.CaptureLog(new TestLogEntry(DateTimeOffset.UtcNow, "Error", "err", []));

        adapter.ClearLogs();

        Assert.Empty(adapter.GetLogs());
    }

    // 8. Logger provider creates a logger that captures log messages
    [Fact]
    public void LoggerProvider_CreatesLogger_ThatCapturesMessages()
    {
        var adapter = CreateAdapter();
        var provider = new TestChannelLoggerProvider(adapter);
        var logger = provider.CreateLogger("TestCategory");

        logger.LogInformation("Hello from test");

        var logs = adapter.GetLogs();
        Assert.Single(logs);
        Assert.Equal("Information", logs[0].Level);
        Assert.Contains("Hello from test", logs[0].Message);
        Assert.Equal("TestCategory", logs[0].Properties["category"]);
    }

    // 9. SendStreamDeltaAsync accumulates and FlushStreamBuffer enqueues
    [Fact]
    public async Task SendStreamDeltaAsync_AccumulatesDeltas_FlushEnqueues()
    {
        var adapter = CreateAdapter();
        await adapter.SendStreamDeltaAsync("conv-1", "Hello ");
        await adapter.SendStreamDeltaAsync("conv-1", "World");

        adapter.FlushStreamBuffer("conv-1");

        var msgs = adapter.GetOutbound("conv-1");
        Assert.Single(msgs);
        Assert.Equal("Hello World", msgs[0].Content);
    }

    // 10. InjectInboundAsync with no dispatcher registered does not throw
    [Fact]
    public async Task InjectInboundAsync_WithNoDispatcher_DoesNotThrow()
    {
        var adapter = CreateAdapter();
        // Adapter not started — dispatcher is null. Should not throw.
        await adapter.InjectInboundAsync("chan-test", "hello", "sender-1");
    }

    // 11. Multiple channels are independent
    [Fact]
    public async Task MultipleChannels_AreIndependent()
    {
        var adapter = CreateAdapter();
        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("chan-a"),
            Content = "for-a",
            Metadata = new Dictionary<string, object?>()
        });
        await adapter.SendAsync(new OutboundMessage
        {
            ChannelType = ChannelKey.From("test"),
            ChannelAddress = ChannelAddress.From("chan-b"),
            Content = "for-b",
            Metadata = new Dictionary<string, object?>()
        });

        var outboundA = adapter.GetOutbound("chan-a");
        var outboundB = adapter.GetOutbound("chan-b");
        Assert.Single(outboundA);
        Assert.Single(outboundB);
        Assert.Equal("for-a", outboundA[0].Content);
        Assert.Equal("for-b", outboundB[0].Content);
    }

    // 12. DisplayName is "Test Channel"
    [Fact]
    public void DisplayName_IsTestChannel()
    {
        var adapter = CreateAdapter();
        Assert.Equal("Test Channel", adapter.DisplayName);
    }
}
