using BotNexus.Gateway.Abstractions.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text;
using System.Text.Json;

namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// Delivers a notification to registered iOS devices through Apple Push Notification service.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="WebPushSender"/> for a native iOS app, which cannot use web push:
/// Apple only wakes a native app for a push that came from APNs. Same broadcaster, same
/// best-effort contract - the notification is in the store before any of this runs, so a failed
/// push costs immediacy and nothing else.
/// </remarks>
public sealed class ApnsSender(
    HttpClient http,
    IApnsDeviceStore store,
    ApnsOptions options,
    ApnsTokenProvider tokens,
    ILogger<ApnsSender>? logger = null)
{
    /// <summary>APNs rejects an alert payload larger than this.</summary>
    private const int MaxPayloadBytes = 4096;

    /// <summary>How long APNs should hold a push for a device that is offline.</summary>
    private static readonly TimeSpan Expiration = TimeSpan.FromHours(24);

    private readonly HttpClient _http = http;
    private readonly IApnsDeviceStore _store = store;
    private readonly ApnsOptions _options = options;
    private readonly ApnsTokenProvider _tokens = tokens;
    private readonly ILogger<ApnsSender> _logger = logger ?? NullLogger<ApnsSender>.Instance;

    /// <summary>Pushes one notification to every registered device. Returns how many were accepted.</summary>
    public async Task<int> SendAsync(Notification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        // An operator with no Apple Developer account is the normal case, not a misconfiguration.
        // Nothing is attempted and nothing is logged.
        if (!_options.IsConfigured)
            return 0;

        var devices = await _store.ListAsync(ct).ConfigureAwait(false);

        if (devices.Count == 0)
            return 0;

        var payload = BuildPayload(notification);
        var delivered = 0;

        foreach (var device in devices)
        {
            if (await SendOneAsync(device, notification, payload, ct).ConfigureAwait(false))
                delivered++;
        }

        return delivered;
    }

    /// <summary>
    /// Builds the aps payload, trimming the body if the whole thing would exceed Apple's limit.
    /// </summary>
    /// <remarks>
    /// An oversize payload is rejected outright with PayloadTooLarge - the notification is not
    /// truncated for you, it simply does not arrive. A long provider error in the body is a
    /// realistic way to hit 4KB, so it is trimmed here rather than lost there.
    /// </remarks>
    internal static byte[] BuildPayload(Notification notification)
    {
        var body = notification.Body;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                aps = new
                {
                    alert = new { title = notification.Title, body },
                    sound = "default",
                    // Lets the app route a tap without a second round trip.
                    category = notification.Kind.ToString(),
                },
                id = notification.Id,
                kind = notification.Kind.ToString(),
                severity = notification.Severity.ToString(),
                link = notification.Link,
            });

            if (bytes.Length <= MaxPayloadBytes || string.IsNullOrEmpty(body))
                return bytes;

            // Halve the body and try again. The title is never trimmed: it is the part written to
            // be read on a lock screen, and a notification with no title is useless.
            var keep = Math.Max(0, body.Length / 2);
            body = keep == 0 ? null : body[..keep];
        }

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            aps = new { alert = new { title = notification.Title } },
            id = notification.Id,
        });
    }

    private async Task<bool> SendOneAsync(
        ApnsDevice device,
        Notification notification,
        byte[] payload,
        CancellationToken ct)
    {
        try
        {
            var url = $"{ApnsEnvironment.HostFor(device.Environment)}/3/device/{device.DeviceToken}";

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                // APNs speaks HTTP/2 only, and refuses the connection rather than negotiating down.
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(payload),
            };

            request.Headers.TryAddWithoutValidation("authorization", $"bearer {_tokens.GetToken()}");
            request.Headers.TryAddWithoutValidation("apns-topic", _options.BundleId);
            request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            request.Headers.TryAddWithoutValidation("apns-priority", "10");
            request.Headers.TryAddWithoutValidation(
                "apns-expiration",
                DateTimeOffset.UtcNow.Add(Expiration).ToUnixTimeSeconds().ToString());

            // Same notification twice replaces the earlier alert rather than stacking a duplicate,
            // matching what the web push tag does.
            if (!string.IsNullOrEmpty(notification.Id))
                request.Headers.TryAddWithoutValidation("apns-collapse-id", Collapse(notification.Id));

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                await _store.MarkDeliveredAsync(device.DeviceToken, ct).ConfigureAwait(false);
                return true;
            }

            await HandleRefusalAsync(device, response, ct).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to push a notification to an iOS device.");
            return false;
        }
    }

    private async Task HandleRefusalAsync(
        ApnsDevice device,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var reason = await ReadReasonAsync(response, ct).ConfigureAwait(false);

        // 410 is Apple saying the app is gone from this device. BadDeviceToken means the token was
        // never valid here - most often a sandbox token sent to production, or the reverse. Both
        // are permanent for this row, and keeping it would retry forever in silence.
        var permanent = response.StatusCode == HttpStatusCode.Gone
            || string.Equals(reason, "BadDeviceToken", StringComparison.Ordinal)
            || string.Equals(reason, "Unregistered", StringComparison.Ordinal);

        if (permanent)
        {
            await _store.RemoveAsync(device.DeviceToken, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Removed an iOS device registration; APNs reported {Status} {Reason}.",
                (int)response.StatusCode,
                reason ?? "(no reason)");

            return;
        }

        // Everything else is the gateway's problem or Apple's, not the device's. A configuration
        // fault is worth saying loudly, because it silently affects EVERY device.
        if (reason is "InvalidProviderToken" or "ExpiredProviderToken" or "TopicDisallowed" or "MissingTopic")
        {
            _logger.LogError(
                "APNs rejected the gateway's credentials with {Reason}. No iOS device will receive "
                + "notifications until gateway:apns is corrected.",
                reason);

            return;
        }

        _logger.LogWarning(
            "APNs refused a notification with {Status} {Reason}.",
            (int)response.StatusCode,
            reason ?? "(no reason)");
    }

    private static async Task<string?> ReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
                return null;

            using var document = JsonDocument.Parse(body);

            return document.RootElement.TryGetProperty("reason", out var reason)
                ? reason.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            // A refusal we cannot parse is still a refusal; the status code carries enough.
            return null;
        }
    }

    /// <summary>APNs caps the collapse identifier at 64 bytes.</summary>
    private static string Collapse(string id) =>
        Encoding.UTF8.GetByteCount(id) <= 64 ? id : id[..64];
}
