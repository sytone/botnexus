using BotNexus.Gateway.Abstractions.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// Delivers a notification to every subscribed browser via its push service.
/// </summary>
/// <remarks>
/// This is the layer that reaches a device with the portal closed - and, later, a phone or desktop
/// app, which subscribe exactly the same way. Delivery is best-effort by design: the notification
/// is already in the store before this runs, so a push service that is down costs immediacy, never
/// the notification itself.
/// </remarks>
public sealed class WebPushSender(
    HttpClient http,
    IPushSubscriptionStore store,
    VapidKeyStore keys,
    ILogger<WebPushSender>? logger = null)
{
    /// <summary>How long a push service should hold the message for a device that is offline.</summary>
    private const int TimeToLiveSeconds = 86_400;

    private readonly HttpClient _http = http;
    private readonly IPushSubscriptionStore _store = store;
    private readonly VapidKeyStore _keys = keys;
    private readonly ILogger<WebPushSender> _logger = logger ?? NullLogger<WebPushSender>.Instance;

    /// <summary>
    /// Pushes one notification to every subscription. Returns how many were accepted.
    /// </summary>
    public async Task<int> SendAsync(Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        var subscriptions = await _store.ListAsync(ct).ConfigureAwait(false);

        if (subscriptions.Count == 0)
            return 0;

        // Only what the service worker needs to draw the toast. The push service can see the
        // SIZE of this and nothing else, so there is no reason to send more than the notice.
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = notification.Id,
            kind = notification.Kind.ToString(),
            severity = notification.Severity.ToString(),
            title = notification.Title,
            body = notification.Body,
            link = notification.Link,
        });

        var delivered = 0;

        foreach (var subscription in subscriptions)
        {
            if (await SendOneAsync(subscription, payload, ct).ConfigureAwait(false))
                delivered++;
        }

        return delivered;
    }

    private async Task<bool> SendOneAsync(
        PushSubscription subscription,
        byte[] payload,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var endpoint))
        {
            _logger.LogWarning("Discarding a push subscription with an unusable endpoint.");
            await _store.RemoveAsync(subscription.Endpoint, ct).ConfigureAwait(false);
            return false;
        }

        try
        {
            var body = WebPushEncryptor.Encrypt(
                Base64Url.Decode(subscription.P256dh),
                Base64Url.Decode(subscription.Auth),
                payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(body),
            };

            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("aes128gcm");
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                new VapidSigner(_keys.Keys).CreateAuthorizationHeader(endpoint));
            request.Headers.TryAddWithoutValidation("TTL", TimeToLiveSeconds.ToString());
            request.Headers.TryAddWithoutValidation("Urgency", "normal");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                await _store.MarkDeliveredAsync(subscription.Endpoint, ct).ConfigureAwait(false);
                return true;
            }

            // 404 and 410 are the push service telling us this device is gone for good - the
            // browser was uninstalled, the permission revoked, or the subscription replaced.
            // Keeping it would mean retrying a dead endpoint on every notification forever.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                await _store.RemoveAsync(subscription.Endpoint, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Removed an expired push subscription; the push service reported {Status}.",
                    (int)response.StatusCode);
            }
            else
            {
                // Everything else - rate limiting, an outage - is transient. The notification is
                // already stored, so the device will see it on its next read.
                _logger.LogWarning(
                    "A push service refused a notification with {Status}.",
                    (int)response.StatusCode);
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push a notification to a subscriber.");
            return false;
        }
    }
}
