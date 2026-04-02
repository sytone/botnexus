using System.Net;
using System.Security.Cryptography;
using System.Text;
using BotNexus.Channels.Slack;
using BotNexus.Core.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BotNexus.Tests.Integration.Tests;

/// <summary>
/// SC-CHN-002: Slack webhook endpoint — full E2E through Gateway pipeline.
/// Registers SlackWebhookHandler in the Gateway, POSTs to /webhooks/slack,
/// and validates URL verification + message routing through the full pipeline.
/// </summary>
public sealed class SlackWebhookE2eTests : IDisposable
{
    private const string SigningSecret = "test-slack-signing-secret";
    private readonly string? _previousHome;
    private readonly string _tempHome;

    public SlackWebhookE2eTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-slack-test-{Guid.NewGuid():N}");
        _previousHome = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _previousHome);
        try { if (Directory.Exists(_tempHome)) Directory.Delete(_tempHome, recursive: true); } catch { }
    }


    [Fact]
    public async Task SlackWebhook_UrlVerification_ReturnsChallengeViaGateway()
    {
        using var factory = CreateSlackGatewayFactory();
        using var client = factory.CreateClient();

        var payload = """{"type":"url_verification","challenge":"test-challenge-123"}""";
        var request = CreateSignedRequest(payload);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("test-challenge-123");
    }

    [Fact]
    public async Task SlackWebhook_ValidEventCallback_Returns200()
    {
        using var factory = CreateSlackGatewayFactory();
        using var client = factory.CreateClient();

        var payload = """
            {
              "type":"event_callback",
              "team_id":"T001",
              "event_id":"Ev001",
              "event":{
                "type":"message",
                "user":"U001",
                "text":"hello via gateway",
                "channel":"C001",
                "ts":"1712000000.000100"
              }
            }
            """;
        var request = CreateSignedRequest(payload);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SlackWebhook_InvalidSignature_Returns401ViaGateway()
    {
        using var factory = CreateSlackGatewayFactory();
        using var client = factory.CreateClient();

        var payload = """{"type":"url_verification","challenge":"abc"}""";
        var request = CreateSignedRequest(payload, useWrongSecret: true);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SlackWebhook_EventCallback_PublishesToMessageBus()
    {
        using var factory = CreateSlackGatewayFactory();
        using var client = factory.CreateClient();

        var bus = factory.Services.GetRequiredService<IMessageBus>();

        var payload = """
            {
              "type":"event_callback",
              "team_id":"T002",
              "event_id":"Ev002",
              "event":{
                "type":"message",
                "user":"U002",
                "text":"integration test message",
                "channel":"C002",
                "ts":"1712000001.000200"
              }
            }
            """;
        var request = CreateSignedRequest(payload);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var published = await bus.ReadAsync(cts.Token);

        published.Channel.Should().Be("slack");
        published.SenderId.Should().Be("U002");
        published.ChatId.Should().Be("C002");
        published.Content.Should().Be("integration test message");
    }

    private static WebApplicationFactory<Program> CreateSlackGatewayFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BotNexus:Gateway:ApiKey"] = string.Empty,
                    });
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();

                    // Register SlackWebhookHandler directly (simulating Slack extension load)
                    services.AddSingleton<IWebhookHandler>(sp =>
                        new SlackWebhookHandler(
                            SigningSecret,
                            sp.GetRequiredService<IMessageBus>(),
                            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SlackWebhookHandler>>()));
                });
            });
    }

    private static HttpRequestMessage CreateSignedRequest(string payload, bool useWrongSecret = false)
    {
        var secret = useWrongSecret ? "wrong-secret" : SigningSecret;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeSlackSignature(secret, timestamp, payload);

        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/slack")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        request.Headers.Add("X-Slack-Signature", signature);
        return request;
    }

    private static string ComputeSlackSignature(string signingSecret, string timestamp, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var data = $"v0:{timestamp}:{payload}";
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return $"v0={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
