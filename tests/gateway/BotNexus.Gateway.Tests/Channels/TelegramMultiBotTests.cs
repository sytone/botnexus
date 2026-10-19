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
using Xunit.Abstractions;

namespace BotNexus.Gateway.Tests.Channels;

public sealed class TelegramMultiBotTests(ITestOutputHelper output)
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
            var message = await WaitForLegacyDispatchAsync(dispatched.Task, adapter, handler);
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

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public async Task LegacyDispatchWait_WhenTimedOut_SnapshotsBeforeCleanupAndPreservesException(int extraRequests)
    {
        using var handler = new PollingHandler(MakeUpdate(10, 42, "private message"));
        using var http = new HttpClient(handler, disposeHandler: false);
        var api = new TelegramBotApiClient(http, "legacy-token", NullLogger.Instance);
        for (var i = 0; i < extraRequests; i++) await api.DeleteWebhookAsync();
        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(new TelegramGatewayOptions { BotToken = "legacy-token", AllowedChatIds = { 42 }, PollingTimeoutSeconds = 1 }),
            new StubHttpClientFactory(_ => new HttpClient(handler, disposeHandler: false)));
        var expected = new TimeoutException("injected deadline sentinel, not a reproduced dispatch timeout");
        var dispatchTask = Task.FromException<InboundMessage>(expected);
        Task? pollingTask = null;
        string? beforeCleanup = null;
        string? beforeCleanupPollingStatus = null;
        var runningBeforeCleanup = false;
        var emitted = new List<string>();
        var recordingOutput = new Mock<ITestOutputHelper>();
        recordingOutput.Setup(helper => helper.WriteLine(It.IsAny<string>())).Callback<string>(text =>
        {
            adapter.IsRunning.ShouldBeTrue("diagnostics must be emitted before cleanup");
            handler.CancelledPoll.Task.IsCompleted.ShouldBeFalse();
            emitted.Add(text);
            output.WriteLine(text);
        });
        var fixture = new TelegramMultiBotTests(recordingOutput.Object);

        var actual = await Should.ThrowAsync<TimeoutException>(() =>
            RunPollingScenarioAsync(adapter, Mock.Of<IChannelDispatcher>(), async () =>
            {
                await handler.PendingPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
                pollingTask = adapter.GetPollingTask("default").ShouldNotBeNull();
                try
                {
                    await fixture.WaitForLegacyDispatchAsync(dispatchTask, adapter, handler);
                }
                finally
                {
                    beforeCleanup = expected.Data[LegacyDispatchSnapshotKey] as string;
                    beforeCleanupPollingStatus = pollingTask.Status.ToString();
                    runningBeforeCleanup = adapter.IsRunning;
                }
            }));

        actual.ShouldBeSameAs(expected);
        runningBeforeCleanup.ShouldBeTrue();
        adapter.IsRunning.ShouldBeFalse();
        pollingTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
        handler.CancelledPoll.Task.IsCompletedSuccessfully.ShouldBeTrue();
        var snapshot = beforeCleanup.ShouldNotBeNull();
        actual.Data[LegacyDispatchSnapshotKey].ShouldBe(snapshot);
        emitted.ShouldHaveSingleItem().ShouldBe(snapshot);
        snapshot.ShouldNotContain("legacy-token");
        snapshot.ShouldNotContain("private message");
        snapshot.ShouldNotContain("https:");
        using var document = JsonDocument.Parse(snapshot);
        var root = document.RootElement;
        root.GetProperty("DispatchStatus").GetString().ShouldBe("Faulted");
        root.GetProperty("AdapterRunning").GetBoolean().ShouldBeTrue();
        root.GetProperty("PollingStatus").GetString().ShouldBe(beforeCleanupPollingStatus.ShouldNotBeNull());
        root.GetProperty("PollCount").GetInt32().ShouldBe(2);
        root.GetProperty("PendingPollReady").GetBoolean().ShouldBeTrue();
        root.GetProperty("PollCancelled").GetBoolean().ShouldBeFalse();
        var operations = root.GetProperty("Operations").EnumerateArray().ToArray();
        operations.Length.ShouldBe(Math.Min(extraRequests + 3, 8));
        operations[0].GetProperty("Operation").GetString().ShouldBe("deleteWebhook");
        operations[^2].GetProperty("Offset").ValueKind.ShouldBe(JsonValueKind.Null);
        operations[^1].GetProperty("Operation").GetString().ShouldBe("getUpdates");
        operations[^1].GetProperty("Offset").GetInt64().ShouldBe(11);
        operations[^1].GetProperty("Timeout").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task LegacyDispatchWait_WhenSuccessful_ReturnsOriginalMessage()
    {
        using var handler = new PollingHandler(MakeUpdate(10, 42, "unused"));
        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(new TelegramGatewayOptions { BotToken = "legacy-token" }),
            new StubHttpClientFactory(_ => new HttpClient(handler, disposeHandler: false)));
        var expected = new InboundMessage
        {
            ChannelType = ChannelKey.From("telegram"),
            ChannelAddress = TelegramChannelAddress.Encode(42, null),
            SenderId = "42",
            Sender = BotNexus.Domain.World.CitizenId.Of(UserId.From("42")),
            Content = "success"
        };
        var actual = await WaitForLegacyDispatchAsync(Task.FromResult(expected), adapter, handler);
        actual.ShouldBeSameAs(expected);
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task LegacyDispatchWait_WhenNotTimeout_DoesNotAttachDiagnostics()
    {
        using var handler = new PollingHandler(MakeUpdate(10, 42, "unused"));
        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(new TelegramGatewayOptions { BotToken = "legacy-token" }),
            new StubHttpClientFactory(_ => new HttpClient(handler, disposeHandler: false)));
        var expected = new InvalidOperationException("non-timeout sentinel");
        var actual = await Should.ThrowAsync<InvalidOperationException>(() =>
            WaitForLegacyDispatchAsync(Task.FromException<InboundMessage>(expected), adapter, handler));
        actual.ShouldBeSameAs(expected);
        actual.Data.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDispatchSnapshot_WhenDispatchPending_CapturesPollingBoundary(bool startPolling)
    {
        // This update is outside the allow-list, so the real dispatch signal stays pending
        // even after the adapter has consumed the update and reached its next long poll.
        var dispatched = new TaskCompletionSource<InboundMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new PollingHandler(MakeUpdate(10, 43, "private message"));
        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(new TelegramGatewayOptions { BotToken = "legacy-token", AllowedChatIds = { 42 }, PollingTimeoutSeconds = 1 }),
            new StubHttpClientFactory(_ => new HttpClient(handler, disposeHandler: false)));
        var dispatcher = new Mock<IChannelDispatcher>();
        dispatcher.Setup(d => d.DispatchAsync(It.IsAny<InboundMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback<InboundMessage, CancellationToken>((message, _) => dispatched.TrySetResult(message));
        Task? pollingTask = null;
        try
        {
            if (startPolling)
            {
                await adapter.StartAsync(dispatcher.Object, CancellationToken.None);
                await handler.PendingPoll.Task.WaitAsync(TimeSpan.FromSeconds(5));
                pollingTask = adapter.GetPollingTask("default").ShouldNotBeNull();
                pollingTask.IsCompleted.ShouldBeFalse();
            }

            dispatched.Task.IsCompleted.ShouldBeFalse();
            var snapshot = CaptureLegacyDispatchSnapshot(dispatched.Task, adapter, handler);
            using var document = JsonDocument.Parse(snapshot);
            var root = document.RootElement;
            root.GetProperty("DispatchStatus").GetString().ShouldBe(dispatched.Task.Status.ToString());
            root.GetProperty("AdapterRunning").GetBoolean().ShouldBe(startPolling);
            root.GetProperty("PollingStatus").GetString().ShouldBe(pollingTask?.Status.ToString() ?? "NotStarted");
            root.GetProperty("PollCount").GetInt32().ShouldBe(startPolling ? 2 : 0);
            root.GetProperty("PendingPollReady").GetBoolean().ShouldBe(startPolling);
            root.GetProperty("PollCancelled").GetBoolean().ShouldBeFalse();
            root.GetProperty("Operations").GetArrayLength().ShouldBe(startPolling ? 3 : 0);
            snapshot.ShouldNotContain("legacy-token");
            snapshot.ShouldNotContain("private message");
            snapshot.ShouldNotContain("https:");
        }
        finally
        {
            await adapter.StopAsync(CancellationToken.None);
        }
        adapter.IsRunning.ShouldBeFalse();
        if (startPolling)
        {
            pollingTask.ShouldNotBeNull().IsCompletedSuccessfully.ShouldBeTrue();
            handler.CancelledPoll.Task.IsCompletedSuccessfully.ShouldBeTrue();
        }
    }

    private const string LegacyDispatchSnapshotKey = "TelegramLegacyDispatchSnapshot";

    private async Task<InboundMessage> WaitForLegacyDispatchAsync(
        Task<InboundMessage> dispatchTask, TelegramChannelAdapter adapter, PollingHandler handler)
    {
        try
        {
            return await dispatchTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException exception)
        {
            // Capture observations before RunPollingScenarioAsync's finally stops the poller.
            // These states do not establish why dispatch missed its deadline. Never serialize
            // request URIs (which contain bot tokens), message bodies, or arbitrary log text.
            var snapshot = CaptureLegacyDispatchSnapshot(dispatchTask, adapter, handler);
            exception.Data[LegacyDispatchSnapshotKey] = snapshot;
            output.WriteLine(snapshot);
            throw;
        }
    }

    // A synchronous observation seam lets boundary tests inspect genuinely pending dispatch
    // without waiting for, shortening, or replacing the real five-second deadline.
    private static string CaptureLegacyDispatchSnapshot(
        Task<InboundMessage> dispatchTask, TelegramChannelAdapter adapter, PollingHandler handler) =>
        JsonSerializer.Serialize(new
        {
            DispatchStatus = dispatchTask.Status.ToString(),
            AdapterRunning = adapter.IsRunning,
            PollingStatus = adapter.GetPollingTask("default")?.Status.ToString() ?? "NotStarted",
            PollCount = handler.Polls,
            PendingPollReady = handler.PendingPoll.Task.IsCompletedSuccessfully,
            PollCancelled = handler.CancelledPoll.Task.IsCompletedSuccessfully,
            Operations = handler.Requests.TakeLast(8).Select(request => new
            {
                Operation = request.Uri.EndsWith("/getUpdates", StringComparison.Ordinal) ? "getUpdates"
                    : request.Uri.EndsWith("/deleteWebhook", StringComparison.Ordinal) ? "deleteWebhook" : "other",
                Offset = request.Body.TryGetProperty("offset", out var offset) && offset.ValueKind == JsonValueKind.Number && offset.TryGetInt64(out var offsetValue)
                    ? (long?)offsetValue : null,
                Timeout = request.Body.TryGetProperty("timeout", out var timeout) && timeout.ValueKind == JsonValueKind.Number && timeout.TryGetInt32(out var timeoutValue)
                    ? (int?)timeoutValue : null
            }).ToArray()
        });

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
