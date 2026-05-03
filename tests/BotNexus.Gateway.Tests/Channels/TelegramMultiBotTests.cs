using System.Net;
using System.Text;
using System.Text.Json;
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
                    AgentId = "larry",
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

        dispatchedMessages.ShouldContain(m => m.TargetAgentId == "larry" && m.Content == "hello from bot1");
        dispatchedMessages.ShouldContain(m => m.TargetAgentId == "assistant" && m.Content == "hello from bot2");
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

        message.TargetAgentId.ShouldBe("legacy-agent");
        message.Content.ShouldBe("legacy message");
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
