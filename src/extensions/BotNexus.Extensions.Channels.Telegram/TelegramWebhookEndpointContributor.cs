using System.Text.Json;
using BotNexus.Gateway.Abstractions.Channels;
using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BotNexus.Extensions.Channels.Telegram;

/// <summary>
/// Maps the inbound Telegram webhook receiver endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Telegram delivers updates by POSTing JSON to a public HTTPS URL registered via <c>setWebhook</c>.
/// This contributor exposes <c>POST /telegram/webhook/{botName}</c> and is the counterpart to the
/// adapter's <c>setWebhook</c> registration: without it, webhook mode registers a URL Telegram posts
/// to but nothing receives the updates.
/// </para>
/// <para>
/// <b>Authentication.</b> The only thing standing between this public endpoint and forged-update
/// injection is the <c>X-Telegram-Bot-Api-Secret-Token</c> header, which Telegram echoes back from
/// the secret registered with <c>setWebhook</c>. Every request is validated against the bot's
/// registered secret in constant time before any update is dispatched; a mismatch returns 403 and
/// the update is dropped. After the secret check, the update flows through the exact same allow-list
/// (<c>allowedChatIds</c>/<c>allowedUserIds</c>) and dispatch path as long polling, so webhook mode
/// is no less restricted than polling.
/// </para>
/// </remarks>
public sealed class TelegramWebhookEndpointContributor : IEndpointContributor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public void MapEndpoints(WebApplication app)
    {
        app.MapPost("/telegram/webhook/{botName}", HandleAsync)
            .WithName("TelegramWebhook")
            .ExcludeFromDescription();
    }

    private static async Task<IResult> HandleAsync(
        string botName,
        HttpRequest request,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var logger = services.GetService<ILogger<TelegramWebhookEndpointContributor>>();

        // The Telegram adapter is registered as a singleton IChannelAdapter; resolving the
        // collection and selecting by channel type yields the same instance the gateway started,
        // so dispatched updates reuse the running adapter's allow-list and routing pipeline.
        var adapter = services.GetServices<IChannelAdapter>()
            .OfType<TelegramChannelAdapter>()
            .FirstOrDefault();

        if (adapter is null)
        {
            // Telegram channel not loaded — nothing to receive for. 404 keeps the endpoint quiet.
            return Results.NotFound();
        }

        var providedSecret = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();

        TelegramUpdate? update;
        try
        {
            update = await JsonSerializer.DeserializeAsync<TelegramUpdate>(request.Body, JsonOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            // Malformed body. Do not echo content back. 400 tells a misbehaving caller; Telegram
            // itself never sends malformed JSON, so this is almost always a probe.
            logger?.LogWarning(ex, "Telegram webhook for bot '{BotName}' received a malformed body", botName);
            return Results.BadRequest();
        }

        if (update is null)
            return Results.BadRequest();

        var result = await adapter.HandleWebhookUpdateAsync(botName, update, providedSecret, cancellationToken);
        return result switch
        {
            TelegramChannelAdapter.WebhookHandleResult.Accepted => Results.Ok(),
            TelegramChannelAdapter.WebhookHandleResult.SecretMismatch => Results.StatusCode(StatusCodes.Status403Forbidden),
            TelegramChannelAdapter.WebhookHandleResult.UnknownBot => Results.NotFound(),
            _ => Results.Ok(),
        };
    }
}
