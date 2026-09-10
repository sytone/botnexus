using BotNexus.Gateway.Notifications.Push;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Lets a browser - or later a phone or desktop app - register for pushed notifications.
/// </summary>
/// <remarks>
/// Three calls, in the order a client makes them: fetch the gateway's public key, hand back the
/// subscription the browser minted with it, and drop it again on the way out. The gateway never
/// creates a subscription; only the browser can, and only after the person using it has granted
/// permission.
/// </remarks>
[ApiController]
[Route("api/notifications/push")]
public sealed class PushSubscriptionsController(
    IPushSubscriptionStore store,
    VapidKeyStore keys) : ControllerBase
{
    private readonly IPushSubscriptionStore _store = store;
    private readonly VapidKeyStore _keys = keys;

    /// <summary>
    /// The gateway's VAPID public key, which a browser needs before it can subscribe.
    /// </summary>
    /// <remarks>
    /// Public by design: it is handed to every subscriber and is what binds a subscription to this
    /// gateway. Knowing it does not let anyone push - that needs the private half.
    /// </remarks>
    [HttpGet("key")]
    [ProducesResponseType(typeof(PushKeyResponse), StatusCodes.Status200OK)]
    public ActionResult<PushKeyResponse> Key() =>
        Ok(new PushKeyResponse { PublicKey = _keys.Keys.PublicKey });

    /// <summary>Registers a subscription, or refreshes one already held.</summary>
    /// <param name="request">The subscription as the Push API produced it.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("subscribe")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Subscribe(
        [FromBody] PushSubscribeRequest request,
        CancellationToken ct = default)
    {
        if (request is null
            || string.IsNullOrWhiteSpace(request.Endpoint)
            || string.IsNullOrWhiteSpace(request.P256dh)
            || string.IsNullOrWhiteSpace(request.Auth))
        {
            return BadRequest(new { error = "endpoint, p256dh and auth are all required." });
        }

        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && !endpoint.IsLoopback))
        {
            // A push endpoint is always https in practice. Refusing anything else keeps the
            // gateway from being pointed at an arbitrary address as a crude request forwarder.
            return BadRequest(new { error = "endpoint must be an absolute https URL." });
        }

        // Checked now rather than at send time, and checked for VALIDITY rather than length: a key
        // that is 65 bytes but not a point on the curve passes a length check, is stored happily,
        // and then throws inside the key agreement on every notification from then on - while the
        // browser that sent it believes it is subscribed. Length alone let exactly that through.
        if (!IsSubscriberKey(request.P256dh) || !IsKeyOfLength(request.Auth, 16))
        {
            return BadRequest(new
            {
                error = "p256dh must be a P-256 public key and auth 16 bytes, both base64url.",
            });
        }

        await _store.SaveAsync(
            new PushSubscription
            {
                Endpoint = request.Endpoint,
                P256dh = request.P256dh,
                Auth = request.Auth,
                UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 0 } ua
                    ? ua[..Math.Min(ua.Length, 256)]
                    : null,
            },
            ct);

        return NoContent();
    }

    /// <summary>Forgets a subscription. Idempotent.</summary>
    /// <param name="request">The endpoint to forget.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("unsubscribe")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Unsubscribe(
        [FromBody] PushUnsubscribeRequest request,
        CancellationToken ct = default)
    {
        // No 404 for an endpoint we do not hold: the caller wanted it gone, and it is gone.
        // A browser that unsubscribes twice should not have to care which call was the real one.
        if (!string.IsNullOrWhiteSpace(request?.Endpoint))
            await _store.RemoveAsync(request.Endpoint, ct);

        return NoContent();
    }

    private static bool IsSubscriberKey(string value)
    {
        try
        {
            return WebPushEncryptor.IsValidSubscriberKey(Base64Url.Decode(value));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsKeyOfLength(string value, int expected)
    {
        try
        {
            return Base64Url.Decode(value).Length == expected;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>The gateway's VAPID public key.</summary>
public sealed class PushKeyResponse
{
    /// <summary>Uncompressed P-256 point, base64url.</summary>
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }
}

/// <summary>A subscription as the browser's Push API produced it.</summary>
public sealed class PushSubscribeRequest
{
    /// <summary>The push service URL.</summary>
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }

    /// <summary>The subscriber public key, base64url.</summary>
    [JsonPropertyName("p256dh")] public string? P256dh { get; init; }

    /// <summary>The subscriber auth secret, base64url.</summary>
    [JsonPropertyName("auth")] public string? Auth { get; init; }
}

/// <summary>The endpoint to forget.</summary>
public sealed class PushUnsubscribeRequest
{
    /// <summary>The push service URL.</summary>
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }
}
