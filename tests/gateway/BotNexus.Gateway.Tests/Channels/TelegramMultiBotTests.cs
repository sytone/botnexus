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

        using var bot1 = new PollingHandler(MakeUpdate(10, 101, "hello from bot1"));
        using var bot2 = new PollingHandler(MakeUpdate(20, 202, "hello from bot2"));

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
            ? new HttpClient(bot1, disposeHandler: false)
            : new HttpClient(bot2, disposeHandler: false));

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

        await RunPollingScenarioAsync(adapter, dispatcher.Object, async () =>
        {
            await twoMessagesSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            dispatchedMessages.ShouldContain(m => m.RoutingHints != null && m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == "agent-b" && m.Content == "hello from bot1");
            dispatchedMessages.ShouldContain(m => m.RoutingHints != null && m.RoutingHints.RequestedAgentId != null && m.RoutingHints.RequestedAgentId.Value.Value == "assistant" && m.Content == "hello from bot2");
        });
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

        using var handler = new PollingHandler(MakeUpdate(10, 42, "legacy message"));
        var httpFactory = new StubHttpClientFactory(_ => new HttpClient(handler, disposeHandler: false));

        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            httpFactory);

        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((m, _) => dispatched.TrySetResult(m));

        await RunPollingScenarioAsync(adapter, dispatcher.Object, async () =>
        {
            var message = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var hints = message.RoutingHints.ShouldNotBeNull();
            hints.RequestedAgentId.ShouldNotBeNull().Value.ShouldBe("legacy-agent");
            message.Content.ShouldBe("legacy message");
        });
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

    /// <summary>
    /// The fake must model a pending long poll, not an unbounded synchronous empty-response loop.
    /// Exercise the real API client so wire serialization and cancellation are covered as well.
    /// </summary>
    [Fact]
    public async Task PollingFixture_AfterInitialUpdate_PendsUntilCancelled()
    {
        using var handler = new PollingHandler(MakeUpdate(10, 42, "legacy message"));
        using var http = new HttpClient(handler);
        var api = new TelegramBotApiClient(http, "legacy-token", NullLogger.Instance);
        await api.DeleteWebhookAsync();
        var updates = await api.GetUpdatesAsync(null, 1);
        var update = updates.ShouldHaveSingleItem();
        update.UpdateId.ShouldBe(10);
        var message = update.Message.ShouldNotBeNull();
        message.Chat.ShouldNotBeNull().Id.ShouldBe(42);
        message.Text.ShouldBe("legacy message");

        using var cancellation = new CancellationTokenSource();
        var pending = api.GetUpdatesAsync(update.UpdateId + 1, 1, cancellation.Token);
        try
        {
            pending.IsCompleted.ShouldBeFalse("the second getUpdates must yield until cancellation");
            await handler.PendingPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
            handler.Polls.ShouldBe(2);
            var requests = handler.Requests.ToArray();
            requests.Length.ShouldBe(3);
            requests.ShouldAllBe(r => r.Method == HttpMethod.Post);
            requests[0].Uri.ShouldBe("https://api.telegram.org/botlegacy-token/deleteWebhook");
            requests[0].Body.GetProperty("drop_pending_updates").GetBoolean().ShouldBeFalse();
            requests[1].Uri.ShouldBe("https://api.telegram.org/botlegacy-token/getUpdates");
            requests[1].Body.GetProperty("offset").ValueKind.ShouldBe(JsonValueKind.Null);
            requests[2].Uri.ShouldBe(requests[1].Uri);
            requests[2].Body.GetProperty("offset").GetInt64().ShouldBe(11);
            foreach (var request in requests.Skip(1))
            {
                request.Body.GetProperty("timeout").GetInt32().ShouldBe(1);
                request.Body.GetProperty("allowed_updates").EnumerateArray()
                    .Select(v => v.GetString()).ShouldContain("message");
            }
        }
        finally
        {
            cancellation.Cancel();
        }

        await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
        handler.CancelledPoll.Task.IsCompletedSuccessfully.ShouldBeTrue();
        handler.Polls.ShouldBe(2);
    }

    /// <summary>
    /// A failing scenario must still cancel and join the adapter's poller before propagating.
    /// </summary>
    [Fact]
    public async Task PollingFixture_WhenScenarioFails_StopsAndJoinsPolling()
    {
        using var handler = new PollingHandler(MakeUpdate(10, 42, "legacy message"));
        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(new TelegramGatewayOptions { BotToken = "legacy-token", AllowedChatIds = { 42 } }),
            new StubHttpClientFactory(_ => new HttpClient(handler, disposeHandler: false)));
        Task? pollingTask = null;
        var expected = new InvalidOperationException("simulated assertion failure");
        try
        {
            var actual = await Should.ThrowAsync<InvalidOperationException>(() =>
                RunPollingScenarioAsync(adapter, Mock.Of<IChannelDispatcher>(), async () =>
                {
                    await handler.PendingPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    pollingTask = adapter.GetPollingTask("default").ShouldNotBeNull();
                    throw expected;
                }));

            actual.ShouldBeSameAs(expected);
            adapter.IsRunning.ShouldBeFalse();
            pollingTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
            handler.CancelledPoll.Task.IsCompletedSuccessfully.ShouldBeTrue();
            handler.Polls.ShouldBe(2);
            handler.Requests.Last().Body.GetProperty("offset").GetInt64().ShouldBe(11);
        }
        finally
        {
            // Keep the regression itself safe even when testing a broken scenario wrapper.
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    private static async Task RunPollingScenarioAsync(
        TelegramChannelAdapter adapter, IChannelDispatcher dispatcher, Func<Task> scenario)
    {
        try
        {
            await adapter.StartAsync(dispatcher, CancellationToken.None);
            await scenario();
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
    }

    private sealed class PollingHandler(TelegramUpdate update) : HttpMessageHandler
    {
        private int _polls;
        public int Polls => Volatile.Read(ref _polls);
        public System.Collections.Concurrent.ConcurrentQueue<(HttpMethod Method, string Uri, JsonElement Body)> Requests { get; } = new();
        public TaskCompletionSource PendingPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelledPoll { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content.ShouldNotBeNull().ReadAsStringAsync(cancellationToken));
            Requests.Enqueue((request.Method, request.RequestUri.ShouldNotBeNull().AbsoluteUri, body.RootElement.Clone()));
            var method = request.RequestUri?.Segments.LastOrDefault()?.Trim('/');
            if (method == "deleteWebhook") return JsonOk(true);
            if (method != "getUpdates") throw new InvalidOperationException($"Unexpected polling fixture request: {method}");
            if (Interlocked.Increment(ref _polls) == 1) return JsonOk(new[] { update });
            // A real long poll yields while Telegram has no update. Empty synchronous replies
            // make the adapter hot-loop and compete with the dispatch continuation (#4942).
            PendingPoll.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested) CancelledPoll.TrySetResult();
            }
            throw new InvalidOperationException("A pending poll can only end through cancellation.");
        }
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
