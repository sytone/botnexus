using System.Text;
using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BotNexus.Channels.Slack;

public sealed class SlackWebhookHandler : IWebhookHandler
{
    private readonly string _signingSecret;
    private readonly IMessageBus _messageBus;
    private readonly ILogger<SlackWebhookHandler> _logger;

    public SlackWebhookHandler(
        string signingSecret,
        IMessageBus messageBus,
        ILogger<SlackWebhookHandler> logger)
    {
        _signingSecret = signingSecret;
        _messageBus = messageBus;
        _logger = logger;
    }

    public string Path => "/webhooks/slack";

    public async Task<IResult> HandleAsync(HttpContext context)
    {
        string payload;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            payload = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);

        if (!SlackRequestVerifier.IsValid(context.Request.Headers, payload, _signingSecret, DateTimeOffset.UtcNow))
        {
            _logger.LogWarning("Rejected Slack webhook request due to invalid signature.");
            return Results.Unauthorized();
        }

        JsonDocument jsonDocument;
        try
        {
            jsonDocument = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return Results.BadRequest("Invalid JSON payload.");
        }

        using (jsonDocument)
        {
            if (!jsonDocument.RootElement.TryGetProperty("type", out var typeElement))
                return Results.BadRequest("Missing payload type.");

            var requestType = typeElement.GetString();

            if (string.Equals(requestType, "url_verification", StringComparison.OrdinalIgnoreCase))
            {
                if (!jsonDocument.RootElement.TryGetProperty("challenge", out var challengeElement))
                    return Results.BadRequest("Missing challenge.");

                var challenge = challengeElement.GetString();
                return string.IsNullOrWhiteSpace(challenge)
                    ? Results.BadRequest("Missing challenge.")
                    : Results.Text(challenge, "text/plain");
            }

            if (string.Equals(requestType, "event_callback", StringComparison.OrdinalIgnoreCase))
            {
                await PublishEventAsync(jsonDocument.RootElement, context.RequestAborted).ConfigureAwait(false);
                return Results.Ok();
            }
        }

        return Results.Ok();
    }

    private async Task PublishEventAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("event", out var eventElement))
            return;

        if (!eventElement.TryGetProperty("type", out var eventTypeElement) ||
            !string.Equals(eventTypeElement.GetString(), "message", StringComparison.OrdinalIgnoreCase))
            return;

        if (eventElement.TryGetProperty("subtype", out var subtypeElement) && subtypeElement.ValueKind != JsonValueKind.Null)
            return;

        if (eventElement.TryGetProperty("bot_id", out var botIdElement) && botIdElement.ValueKind != JsonValueKind.Null)
            return;

        var channelId = TryGetString(eventElement, "channel");
        var userId = TryGetString(eventElement, "user");
        if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(userId))
            return;

        var inbound = new InboundMessage(
            Channel: "slack",
            SenderId: userId,
            ChatId: channelId,
            Content: TryGetString(eventElement, "text") ?? string.Empty,
            Timestamp: DateTimeOffset.UtcNow,
            Media: [],
            Metadata: new Dictionary<string, object>
            {
                ["team_id"] = TryGetString(root, "team_id") ?? string.Empty,
                ["event_id"] = TryGetString(root, "event_id") ?? string.Empty,
                ["ts"] = TryGetString(eventElement, "ts") ?? string.Empty
            });

        await _messageBus.PublishAsync(inbound, cancellationToken).ConfigureAwait(false);
    }

    private static string? TryGetString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
