using System.Security.Cryptography;
using System.Text;
using BotNexus.Channels.Slack;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Bus;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Tests.Unit.Tests;

public class SlackWebhookHandlerTests
{
    private const string SigningSecret = "test-signing-secret";

    [Fact]
    public async Task HandleAsync_UrlVerification_ReturnsChallenge()
    {
        var bus = new MessageBus();
        var handler = new SlackWebhookHandler(SigningSecret, bus, NullLogger<SlackWebhookHandler>.Instance);
        var payload = "{\"type\":\"url_verification\",\"challenge\":\"abc123\"}";

        var context = CreateSignedContext(payload, SigningSecret);
        var result = await handler.HandleAsync(context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        (await ReadResponseBodyAsync(context)).Should().Be("abc123");
    }

    [Fact]
    public async Task HandleAsync_EventCallback_MessageEvent_PublishesInboundMessage()
    {
        var bus = new MessageBus();
        var handler = new SlackWebhookHandler(SigningSecret, bus, NullLogger<SlackWebhookHandler>.Instance);
        var payload = """
            {
              "type":"event_callback",
              "team_id":"T001",
              "event_id":"Ev001",
              "event":{
                "type":"message",
                "user":"U001",
                "text":"hello from slack",
                "channel":"C001",
                "ts":"1712000000.000100"
              }
            }
            """;

        var context = CreateSignedContext(payload, SigningSecret);
        var result = await handler.HandleAsync(context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var published = await bus.ReadAsync(cts.Token);

        published.Channel.Should().Be("slack");
        published.SenderId.Should().Be("U001");
        published.ChatId.Should().Be("C001");
        published.Content.Should().Be("hello from slack");
        published.Metadata["team_id"].Should().Be("T001");
        published.Metadata["event_id"].Should().Be("Ev001");
    }

    [Fact]
    public async Task HandleAsync_ValidSignature_IsAccepted()
    {
        var bus = new MessageBus();
        var handler = new SlackWebhookHandler(SigningSecret, bus, NullLogger<SlackWebhookHandler>.Instance);
        var payload = "{\"type\":\"event_callback\",\"event\":{\"type\":\"reaction_added\"}}";

        var context = CreateSignedContext(payload, SigningSecret);
        var result = await handler.HandleAsync(context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task HandleAsync_InvalidSignature_IsRejected()
    {
        var bus = new MessageBus();
        var handler = new SlackWebhookHandler(SigningSecret, bus, NullLogger<SlackWebhookHandler>.Instance);
        var payload = "{\"type\":\"url_verification\",\"challenge\":\"abc123\"}";

        var context = CreateSignedContext(payload, "wrong-secret");
        var result = await handler.HandleAsync(context);
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public void SlackExtensionRegistrar_RegistersWebhookOnlyWhenEnabled()
    {
        var enabledConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Enabled"] = "true",
                ["BotToken"] = "xoxb-token",
                ["SigningSecret"] = SigningSecret
            })
            .Build();
        var disabledConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Enabled"] = "false"
            })
            .Build();

        var enabledServices = new ServiceCollection();
        enabledServices.AddLogging();
        enabledServices.AddSingleton<IMessageBus>(new MessageBus());
        new SlackExtensionRegistrar().Register(enabledServices, enabledConfig);
        enabledServices.Should().Contain(sd => sd.ServiceType == typeof(IWebhookHandler));

        var disabledServices = new ServiceCollection();
        disabledServices.AddLogging();
        disabledServices.AddSingleton<IMessageBus>(new MessageBus());
        new SlackExtensionRegistrar().Register(disabledServices, disabledConfig);
        disabledServices.Should().NotContain(sd => sd.ServiceType == typeof(IWebhookHandler));
    }

    private static DefaultHttpContext CreateSignedContext(string payload, string secret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = ComputeSignature(secret, timestamp, payload);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers["X-Slack-Request-Timestamp"] = timestamp;
        context.Request.Headers["X-Slack-Signature"] = signature;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        context.Response.Body = new MemoryStream();
        context.RequestServices = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();
        return context;
    }

    private static string ComputeSignature(string signingSecret, string timestamp, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var data = $"v0:{timestamp}:{payload}";
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return $"v0={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static async Task<string> ReadResponseBodyAsync(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}
