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

/// <summary>
/// Tests for the inbound Telegram webhook handling path on <see cref="TelegramChannelAdapter"/>:
/// secret-token registration via <c>setWebhook</c>, constant-time secret validation, allow-list
/// reuse, and dispatch routing identical to long polling.
/// </summary>
public sealed class TelegramWebhookHandlerTests
{
    [Fact]
    public async Task StartAsync_WebhookMode_RegistersSecretTokenWithTelegram()
    {
        var (adapter, getSecret) = await StartWebhookAdapterAsync(new TelegramGatewayOptions
        {
            BotToken = "token",
            WebhookUrl = "https://example.com/telegram/webhook/default"
        });

        // setWebhook must have carried a non-empty secret_token so the receiver can authenticate.
        getSecret().ShouldNotBeNullOrWhiteSpace();
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WebhookMode_UsesConfiguredSecretWhenValid()
    {
        const string configured = "my-configured-secret_123";
        var (adapter, getSecret) = await StartWebhookAdapterAsync(new TelegramGatewayOptions
        {
            BotToken = "token",
            WebhookUrl = "https://example.com/telegram/webhook/default",
            WebhookSecretToken = configured
        });

        getSecret().ShouldBe(configured);
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_WebhookMode_GeneratesSecretWhenConfiguredOneIsInvalid()
    {
        var (adapter, getSecret) = await StartWebhookAdapterAsync(new TelegramGatewayOptions
        {
            BotToken = "token",
            WebhookUrl = "https://example.com/telegram/webhook/default",
            WebhookSecretToken = "invalid secret with spaces"
        });

        var actual = getSecret();
        actual.ShouldNotBeNullOrWhiteSpace();
        actual.ShouldNotBe("invalid secret with spaces");
        TelegramWebhookSecret.IsValid(actual).ShouldBeTrue();
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleWebhookUpdate_WithValidSecret_DispatchesThroughNormalPipeline()
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        var (adapter, getSecret) = await StartWebhookAdapterAsync(
            new TelegramGatewayOptions
            {
                BotToken = "token",
                WebhookUrl = "https://example.com/telegram/webhook/default",
                AllowedChatIds = { 42 },
                AllowedUserIds = { 7 }
            },
            dispatcher);

        var result = await adapter.HandleWebhookUpdateAsync(
            "default",
            BuildUpdate(chatId: 42, userId: 7, text: "hello"),
            getSecret(),
            CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.Accepted);
        dispatcher.Invocations
            .Where(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync))
            .Select(i => (InboundMessage)i.Arguments[0])
            .ShouldContain(m => m.ChannelAddress == ChannelAddress.From("42") && m.Content == "hello");

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleWebhookUpdate_WithWrongSecret_RejectsAndDoesNotDispatch()
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        var (adapter, _) = await StartWebhookAdapterAsync(
            new TelegramGatewayOptions
            {
                BotToken = "token",
                WebhookUrl = "https://example.com/telegram/webhook/default"
            },
            dispatcher);

        var result = await adapter.HandleWebhookUpdateAsync(
            "default",
            BuildUpdate(chatId: 42, userId: 7, text: "hello"),
            providedSecret: "totally-wrong-secret",
            CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.SecretMismatch);
        dispatcher.Invocations
            .ShouldNotContain(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync));

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleWebhookUpdate_WithMissingSecret_RejectsAndDoesNotDispatch()
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        var (adapter, _) = await StartWebhookAdapterAsync(
            new TelegramGatewayOptions
            {
                BotToken = "token",
                WebhookUrl = "https://example.com/telegram/webhook/default"
            },
            dispatcher);

        var result = await adapter.HandleWebhookUpdateAsync(
            "default",
            BuildUpdate(chatId: 42, userId: 7, text: "hello"),
            providedSecret: null,
            CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.SecretMismatch);
        dispatcher.Invocations
            .ShouldNotContain(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync));

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleWebhookUpdate_EnforcesChatAllowList_EvenWithValidSecret()
    {
        var dispatcher = new Mock<IChannelDispatcher>();
        var (adapter, getSecret) = await StartWebhookAdapterAsync(
            new TelegramGatewayOptions
            {
                BotToken = "token",
                WebhookUrl = "https://example.com/telegram/webhook/default",
                AllowedChatIds = { 42 }
            },
            dispatcher);

        // Correct secret but a chat that is not on the allow-list — must be dropped silently.
        var result = await adapter.HandleWebhookUpdateAsync(
            "default",
            BuildUpdate(chatId: 9999, userId: 7, text: "intruder"),
            getSecret(),
            CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.Accepted); // accepted = not a secret failure
        dispatcher.Invocations
            .ShouldNotContain(i => i.Method.Name == nameof(IChannelDispatcher.DispatchAsync));

        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleWebhookUpdate_ForUnknownBot_ReturnsUnknownBot()
    {
        var (adapter, _) = await StartWebhookAdapterAsync(new TelegramGatewayOptions
        {
            BotToken = "token",
            WebhookUrl = "https://example.com/telegram/webhook/default"
        });

        var result = await adapter.HandleWebhookUpdateAsync(
            "nonexistent-bot",
            BuildUpdate(chatId: 42, userId: 7, text: "hello"),
            providedSecret: "anything",
            CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.UnknownBot);
        await adapter.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task HandleWebhookUpdate_ForPollingModeBot_ReturnsUnknownBot()
    {
        // Polling-mode bot has no registered secret; webhook delivery to it must be treated as
        // unknown rather than opening an unauthenticated dispatch path.
        var handler = new RecordingHandler();
        var adapter = CreateAdapter(new TelegramGatewayOptions
        {
            BotToken = "token",
            PollingTimeoutSeconds = 1
        }, handler);

        var dispatcher = new Mock<IChannelDispatcher>();
        await adapter.StartAsync(dispatcher.Object, CancellationToken.None);

        var result = await adapter.HandleWebhookUpdateAsync(
            "default",
            BuildUpdate(chatId: 42, userId: 7, text: "hello"),
            providedSecret: "anything",
            CancellationToken.None);

        result.ShouldBe(TelegramChannelAdapter.WebhookHandleResult.UnknownBot);
        await adapter.StopAsync(CancellationToken.None);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static TelegramUpdate BuildUpdate(long chatId, long userId, string text)
        => new()
        {
            UpdateId = 1,
            Message = new TelegramMessage
            {
                MessageId = 1,
                Chat = new TelegramChat { Id = chatId },
                From = new TelegramUser { Id = userId },
                Text = text
            }
        };

    /// <summary>
    /// Starts an adapter in webhook mode and returns it plus an accessor for the secret_token that
    /// was sent to Telegram's setWebhook (captured from the request body).
    /// </summary>
    private static async Task<(TelegramChannelAdapter Adapter, Func<string?> GetSecret)> StartWebhookAdapterAsync(
        TelegramGatewayOptions options,
        Mock<IChannelDispatcher>? dispatcher = null)
    {
        string? capturedSecret = null;
        var handler = new RecordingHandler(request =>
        {
            // setWebhook is the only call this adapter makes at startup in webhook mode.
        }, onSetWebhookSecret: s => capturedSecret = s);

        var adapter = CreateAdapter(options, handler);
        await adapter.StartAsync((dispatcher ?? new Mock<IChannelDispatcher>()).Object, CancellationToken.None);
        return (adapter, () => capturedSecret);
    }

    private static TelegramChannelAdapter CreateAdapter(TelegramGatewayOptions options, HttpMessageHandler handler)
    {
        var factory = new SingleClientFactory(new HttpClient(handler));
        return new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            factory);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    /// <summary>
    /// Stub handler that returns <c>{"ok":true,"result":true}</c> for every Bot API call and
    /// extracts the <c>secret_token</c> from any <c>setWebhook</c> request.
    /// </summary>
    private sealed class RecordingHandler(
        Action<HttpRequestMessage>? onRequest = null,
        Action<string?>? onSetWebhookSecret = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest?.Invoke(request);
            var method = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);

            if (method == "setWebhook")
            {
                using var json = JsonDocument.Parse(body);
                var secret = json.RootElement.TryGetProperty("secret_token", out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null;
                onSetWebhookSecret?.Invoke(secret);
            }

            var payload = method switch
            {
                "getUpdates" => "{\"ok\":true,\"result\":[]}",
                _ => "{\"ok\":true,\"result\":true}"
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }
}
