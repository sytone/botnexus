using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BotNexus.Extensions.Channels.Telegram;
using BotNexus.Gateway.Abstractions.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Channels;

/// <summary>
/// HTTP-level tests for <see cref="TelegramWebhookEndpointContributor"/>, hosting only the
/// webhook endpoint in an in-memory <see cref="TestServer"/> and asserting status-code mapping
/// for valid, invalid, missing-secret, unknown-bot, and malformed-body requests.
/// </summary>
public sealed class TelegramWebhookEndpointContributorTests
{
    private const string ValidUpdateJson =
        "{\"update_id\":1,\"message\":{\"message_id\":1,\"chat\":{\"id\":42},\"from\":{\"id\":7},\"text\":\"hello\"}}";

    [Fact]
    public async Task Post_WithValidSecret_Returns200()
    {
        var (host, secret) = await StartHostAsync();
        await using var _ = host;
        using var client = host.GetTestClient();

        using var response = await PostAsync(client, "default", secret, ValidUpdateJson);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_WithWrongSecret_Returns403()
    {
        var (host, _) = await StartHostAsync();
        await using var __ = host;
        using var client = host.GetTestClient();

        using var response = await PostAsync(client, "default", "wrong-secret", ValidUpdateJson);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_WithoutSecretHeader_Returns403()
    {
        var (host, _) = await StartHostAsync();
        await using var __ = host;
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/telegram/webhook/default")
        {
            Content = new StringContent(ValidUpdateJson, Encoding.UTF8, "application/json")
        };
        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Post_ForUnknownBot_Returns404()
    {
        var (host, secret) = await StartHostAsync();
        await using var _ = host;
        using var client = host.GetTestClient();

        using var response = await PostAsync(client, "nonexistent", secret, ValidUpdateJson);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_WithMalformedBody_Returns400()
    {
        var (host, secret) = await StartHostAsync();
        await using var _ = host;
        using var client = host.GetTestClient();

        using var response = await PostAsync(client, "default", secret, "{ this is not valid json");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Host setup ────────────────────────────────────────────────────────────

    private static async Task<(WebApplication Host, string Secret)> StartHostAsync()
    {
        // Capture the secret the adapter registers with Telegram during StartAsync.
        string? capturedSecret = null;
        var handler = new SetWebhookCapturingHandler(s => capturedSecret = s);

        var options = new TelegramGatewayOptions
        {
            BotToken = "token",
            WebhookUrl = "https://example.com/telegram/webhook/default",
            AllowedChatIds = { 42 },
            AllowedUserIds = { 7 }
        };

        var adapter = new TelegramChannelAdapter(
            NullLogger<TelegramChannelAdapter>.Instance,
            Options.Create(options),
            new SingleClientFactory(new HttpClient(handler)));

        // Start the adapter so it registers the webhook (and its secret) and is ready to dispatch.
        await adapter.StartAsync(new Mock<IChannelDispatcher>().Object, CancellationToken.None);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IChannelAdapter>(adapter);
        builder.Services.AddRouting();

        var app = builder.Build();
        new TelegramWebhookEndpointContributor().MapEndpoints(app);
        await app.StartAsync();

        var secret = capturedSecret ?? throw new InvalidOperationException("setWebhook did not capture a secret token.");
        return (app, secret);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string botName, string secret, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/telegram/webhook/{botName}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
        return client.SendAsync(request);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class SetWebhookCapturingHandler(Action<string?> onSecret) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var method = request.RequestUri?.Segments.LastOrDefault()?.Trim('/') ?? string.Empty;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (method == "setWebhook")
            {
                using var json = JsonDocument.Parse(body);
                onSecret(json.RootElement.TryGetProperty("secret_token", out var el) && el.ValueKind == JsonValueKind.String
                    ? el.GetString()
                    : null);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":true}", Encoding.UTF8, "application/json")
            };
        }
    }
}
