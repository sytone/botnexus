using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using System.Net;
using System.Text.Json;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// Tests for streaming state and error reply state eviction in the Telegram channel adapter.
/// Verifies that in-memory collections do not grow unbounded over time.
/// </summary>
public sealed class TelegramStateEvictionTests
{
    private static TelegramChannelAdapter CreateAdapter(HttpMessageHandler handler, int errorCooldownMs = 5000)
    {
        var options = Options.Create(new TelegramGatewayOptions
        {
            BotToken = "123:ABC",
            ErrorCooldownMs = errorCooldownMs,
            AllowedChatIds = { 41, 42, 43, 44, 45, 46 }
        });
        var factory = new StubHttpClientFactory(_ => new HttpClient(handler));
        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            options,
            factory);
    }

    private static StubHttpMessageHandler CreateOkHandler()
    {
        return new StubHttpMessageHandler((request, _) =>
        {
            // Determine which Telegram API method is being called from the URL path
            var path = request.RequestUri?.AbsolutePath ?? "";
            string json;

            if (path.Contains("/sendMessage") || path.Contains("/editMessageText"))
            {
                // Return a valid TelegramMessage-shaped response
                json = JsonSerializer.Serialize(new
                {
                    ok = true,
                    result = new
                    {
                        message_id = 1,
                        chat = new { id = 42 },
                        date = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        text = "ok"
                    }
                });
            }
            else
            {
                json = JsonSerializer.Serialize(new { ok = true, result = true });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        });
    }

    private static async Task StartAdapterAsync(TelegramChannelAdapter adapter)
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
    }

    private static ChannelStreamTarget MakeTarget(int chatId)
    {
        return new ChannelStreamTarget(
            ConversationId.From($"conv-{chatId}"),
            SessionId.From($"session-{chatId}"),
            ChannelAddress.From($"{chatId}"));
    }

    [Fact]
    public async Task StreamingState_RemovedAfterMessageEnd()
    {
        var handler = CreateOkHandler();
        var adapter = CreateAdapter(handler);
        await StartAdapterAsync(adapter);

        var target = MakeTarget(42);

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart }, CancellationToken.None);
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello" }, CancellationToken.None);
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }, CancellationToken.None);

        Assert.Equal(0, adapter.GetStreamingStateCount());
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StreamingState_RetainedDuringActiveStream()
    {
        var handler = CreateOkHandler();
        var adapter = CreateAdapter(handler);
        await StartAdapterAsync(adapter);

        var target = MakeTarget(42);

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart }, CancellationToken.None);
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hello" }, CancellationToken.None);

        Assert.Equal(1, adapter.GetStreamingStateCount());
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StreamingState_MultipleSessionsAllEvictedAfterEnd()
    {
        var handler = CreateOkHandler();
        var adapter = CreateAdapter(handler);
        await StartAdapterAsync(adapter);

        for (int i = 1; i <= 5; i++)
        {
            var target = MakeTarget(40 + i);

            await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart }, CancellationToken.None);
            await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = $"msg-{i}" }, CancellationToken.None);
            await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }, CancellationToken.None);
        }

        Assert.Equal(0, adapter.GetStreamingStateCount());
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LastErrorReplyState_EvictsStaleEntries()
    {
        var handler = CreateOkHandler();
        var adapter = CreateAdapter(handler, errorCooldownMs: 100);
        await StartAdapterAsync(adapter);

        for (int i = 1; i <= 3; i++)
        {
            var target = MakeTarget(40 + i);

            await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart }, CancellationToken.None);
            await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.Error, ErrorMessage = "test error" }, CancellationToken.None);
            await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }, CancellationToken.None);
        }

        // Force eviction of all entries (maxAge=Zero means everything is stale)
        adapter.EvictStaleErrorState(TimeSpan.Zero);

        Assert.Equal(0, adapter.GetErrorReplyStateCount());
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LastErrorReplyState_RetainsRecentEntries()
    {
        var handler = CreateOkHandler();
        var adapter = CreateAdapter(handler, errorCooldownMs: 100);
        await StartAdapterAsync(adapter);

        var target = MakeTarget(42);

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart }, CancellationToken.None);
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.Error, ErrorMessage = "test" }, CancellationToken.None);
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }, CancellationToken.None);

        // Evict only entries older than 1 hour (recent should survive)
        adapter.EvictStaleErrorState(TimeSpan.FromHours(1));

        Assert.Equal(1, adapter.GetErrorReplyStateCount());
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StreamingState_NewStreamOnSameChannelAfterEviction_Works()
    {
        var handler = CreateOkHandler();
        var adapter = CreateAdapter(handler);
        await StartAdapterAsync(adapter);

        var target = MakeTarget(42);

        // First stream
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart }, CancellationToken.None);
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "first" }, CancellationToken.None);
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }, CancellationToken.None);
        Assert.Equal(0, adapter.GetStreamingStateCount());

        // Second stream on same address
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart }, CancellationToken.None);
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "second" }, CancellationToken.None);
        Assert.Equal(1, adapter.GetStreamingStateCount());

        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd }, CancellationToken.None);
        Assert.Equal(0, adapter.GetStreamingStateCount());

        await adapter.StopAsync(CancellationToken.None);
    }

    private sealed class StubHttpClientFactory(Func<string, HttpClient> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => factory(name);
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handler(request, cancellationToken);
    }
}
