using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Channels;

public sealed class TelegramMultiBotTests
{
    /// <summary>
    /// When two bots are configured, messages from each arrive with the correct
    /// TargetAgentId and ChannelAddress.
    /// </summary>
    [Fact]
    public async Task MultiBotConfig_EachBot_RoutesToCorrectAgent()
    {
        var dispatchedMessages = new List<InboundMessage>();
        var twoMessagesSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Bot 1 returns one update; bot 2 returns one update; subsequent polls empty.
        var bot1Polls = 0;
        var bot2Polls = 0;

        HttpResponseMessage Bot1Handler(HttpRequestMessage req)
        {
            var method = req.RequestUri?.Segments.LastOrDefault()?.Trim('/');
            if (method == "deleteWebhook") return JsonOk(true);
            if (method == "getUpdates")
            {
                bot1Polls++;
                if (bot1Polls == 1)
                    return JsonOk(new[] { MakeUpdate(10, 101, "hello from bot1") });
                return JsonOk(Array.Empty<TelegramUpdate>());
            }
            return JsonOk(true);
        }

        HttpResponseMessage Bot2Handler(HttpRequestMessage req)
        {
            var method = req.RequestUri?.Segments.LastOrDefault()?.Trim('/');
            if (method == "deleteWebhook") return JsonOk(true);
            if (method == "getUpdates")
            {
                bot2Polls++;
                if (bot2Polls == 1)
                    return JsonOk(new[] { MakeUpdate(20, 202, "hello from bot2") });
                return JsonOk(Array.Empty<TelegramUpdate>());
            }
            return JsonOk(true);
        }

        var options = new TelegramGatewayOptions
        {
            Bots =
            {
                ["larry-bot"] = new TelegramBotConfig
                {
                    BotToken = "token-larry",
                    AgentId = "agent-b",
                    AllowedChatIds = { 101 },
                    PollingTimeoutSeconds = 1
                },
                ["assistant-bot"] = new TelegramBotConfig
                {
                    BotToken = "token-assistant",
                    AgentId = "assistant",
                    AllowedChatIds = { 202 },
                    PollingTimeoutSeconds = 1
                }
            }
        };

        var httpFactory = new StubHttpClientFactory(name => name == "larry-bot"
            ? new HttpClient(new StubHandler(req => Task.FromResult(Bot1Handler(req))))
            : new HttpClient(new StubHandler(req => Task.FromResult(Bot2Handler(req)))));

        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            httpFactory);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) =>
            {
                lock (dispatchedMessages) dispatchedMessages.Add(m);
                if (dispatchedMessages.Count >= 2) twoMessagesSeen.TrySetResult();
            });

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        await twoMessagesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        dispatchedMessages.ShouldContain(m => m.RoutingHints != null && m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == "agent-b" && m.Content == "hello from bot1");
        dispatchedMessages.ShouldContain(m => m.RoutingHints != null && m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == "assistant" && m.Content == "hello from bot2");
    }

    /// <summary>
    /// When only legacy single-bot fields are set (no Bots dict), the adapter still works.
    /// </summary>
    [Fact]
    public async Task SingleBotLegacyConfig_StillWorks()
    {
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new TelegramGatewayOptions
        {
            BotToken = "legacy-token",
            AgentId = "legacy-agent",
            AllowedChatIds = { 42 },
            PollingTimeoutSeconds = 1
        };

        var httpFactory = new StubHttpClientFactory(_ =>
        {
            var polls = 0;
            return new HttpClient(new StubHandler(req =>
            {
                var method = req.RequestUri?.Segments.LastOrDefault()?.Trim('/');
                if (method == "deleteWebhook") return Task.FromResult(JsonOk(true));
                if (method == "getUpdates")
                {
                    polls++;
                    return Task.FromResult(polls == 1
                        ? JsonOk(new[] { MakeUpdate(10, 42, "legacy message") })
                        : JsonOk(Array.Empty<TelegramUpdate>()));
                }
                return Task.FromResult(JsonOk(true));
            }));
        });

        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            httpFactory);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
        var message = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await adapter.StopAsync(CancellationToken.None);

        message.RoutingHints.ShouldNotBeNull();
        message.RoutingHints!.RequestedAgentId!.Value.Value.ShouldBe("legacy-agent");
        message.Content.ShouldBe("legacy message");
    }

    /// <summary>
    /// Regression for the multi-bot streaming reply bug: when two bots are configured,
    /// a streamed reply must be routed to the bot whose configured agent matches the
    /// stream event's AgentId — not rejected by the old single-bot-only resolver.
    /// </summary>
    [Fact]
    public async Task SendStreamEvent_MultiBot_RoutesReplyToBotMatchingAgent()
    {
        var larrySends = new List<string>();
        var assistantSends = new List<string>();

        HttpResponseMessage Handler(HttpRequestMessage req, List<string> sendSink)
        {
            var method = req.RequestUri?.Segments.LastOrDefault()?.Trim('/');
            if (method == "sendMessage")
            {
                sendSink.Add(req.RequestUri!.AbsoluteUri);
                return JsonOk(new TelegramMessage { MessageId = 999, Chat = new TelegramChat { Id = 202 } });
            }
            if (method == "getUpdates") return JsonOk(Array.Empty<TelegramUpdate>());
            return JsonOk(true);
        }

        var options = new TelegramGatewayOptions
        {
            Bots =
            {
                ["larry-bot"] = new TelegramBotConfig
                {
                    BotToken = "token-larry",
                    AgentId = "agent-b",
                    AllowedChatIds = { 101 },
                    PollingTimeoutSeconds = 1,
                    // This test verifies per-agent bot routing, not rendering. Use the legacy
                    // sendMessage path so the handler applies; Rich Markdown streaming is covered
                    // by TelegramChannelAdapterRichTests.
                    RichMessages = false
                },
                ["assistant-bot"] = new TelegramBotConfig
                {
                    BotToken = "token-assistant",
                    AgentId = "assistant",
                    AllowedChatIds = { 202 },
                    PollingTimeoutSeconds = 1,
                    RichMessages = false
                }
            }
        };

        var httpFactory = new StubHttpClientFactory(name => name == "larry-bot"
            ? new HttpClient(new StubHandler(req => Task.FromResult(Handler(req, larrySends))))
            : new HttpClient(new StubHandler(req => Task.FromResult(Handler(req, assistantSends)))));

        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            httpFactory);

        var target = new ChannelStreamTarget(
            ConversationId.From("conv-1"),
            SessionId.From("sess-1"),
            TelegramChannelAddress.Encode(202, null));

        var assistant = AgentId.From("assistant");
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageStart, AgentId = assistant });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.ContentDelta, ContentDelta = "hi there", AgentId = assistant });
        await adapter.SendStreamEventAsync(target, new AgentStreamEvent { Type = AgentStreamEventType.MessageEnd, AgentId = assistant });

        assistantSends.ShouldNotBeEmpty();
        assistantSends.ShouldContain(u => u.Contains("token-assistant"));
        larrySends.ShouldBeEmpty();
    }

    /// <summary>
    /// Regression for #1681: a non-streaming <see cref="OutboundMessage"/> (fan-out path) carries no
    /// <c>telegramBotName</c> metadata. The old multi-bot resolver threw and dropped the reply.
    /// When the target chat is admitted by exactly one configured bot's allow-list, the send must
    /// degrade gracefully and route through that bot instead of throwing.
    /// </summary>
    [Fact]
    public async Task SendAsync_MultiBot_NoBotNameMetadata_RoutesByAllowList()
    {
        var larrySends = new List<string>();
        var assistantSends = new List<string>();

        HttpResponseMessage Handler(HttpRequestMessage req, List<string> sendSink)
        {
            var method = req.RequestUri?.Segments.LastOrDefault()?.Trim('/');
            if (method == "sendMessage")
            {
                sendSink.Add(req.RequestUri!.AbsoluteUri);
                return JsonOk(new TelegramMessage { MessageId = 999, Chat = new TelegramChat { Id = 202 } });
            }
            if (method == "getUpdates") return JsonOk(Array.Empty<TelegramUpdate>());
            return JsonOk(true);
        }

        // Distinct allow-lists so chat 202 is admitted by exactly one bot (assistant-bot).
        var options = new TelegramGatewayOptions
        {
            Bots =
            {
                ["larry-bot"] = new TelegramBotConfig
                {
                    BotToken = "token-larry",
                    AgentId = "agent-b",
                    AllowedChatIds = { 101 },
                    PollingTimeoutSeconds = 1,
                    RichMessages = false
                },
                ["assistant-bot"] = new TelegramBotConfig
                {
                    BotToken = "token-assistant",
                    AgentId = "assistant",
                    AllowedChatIds = { 202 },
                    PollingTimeoutSeconds = 1,
                    RichMessages = false
                }
            }
        };

        var httpFactory = new StubHttpClientFactory(name => name == "larry-bot"
            ? new HttpClient(new StubHandler(req => Task.FromResult(Handler(req, larrySends))))
            : new HttpClient(new StubHandler(req => Task.FromResult(Handler(req, assistantSends)))));

        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            httpFactory);

        // No telegramBotName metadata -- exactly the fan-out OutboundMessage shape that used to throw.
        var outbound = new OutboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = TelegramChannelAddress.Encode(202, null),
            Content = "delivered via allow-list fallback"
        };

        // Must NOT throw, and must route through assistant-bot (the only bot allowing chat 202).
        await Should.NotThrowAsync(async () => await adapter.SendAsync(outbound, CancellationToken.None));

        assistantSends.ShouldNotBeEmpty();
        assistantSends.ShouldContain(u => u.Contains("token-assistant"));
        larrySends.ShouldBeEmpty();
    }

    private static TelegramUpdate MakeUpdate(int updateId, long chatId, string text) =>
        new()
        {
            UpdateId = updateId,
            Message = new TelegramMessage
            {
                MessageId = updateId * 10,
                Chat = new TelegramChat { Id = chatId },
                From = new TelegramUser { Id = chatId },
                Text = text
            }
        };

    private static HttpResponseMessage JsonOk<T>(T result)
    {
        var payload = JsonSerializer.Serialize(new TelegramApiResponse<T> { Ok = true, Result = result });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }

    private sealed class StubHttpClientFactory(Func<string, HttpClient> factory) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => factory(name);
    }
}
