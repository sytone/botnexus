namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// One browser's standing request to be pushed notifications.
/// </summary>
/// <remarks>
/// Created by the browser, not by the gateway: the three values below come straight from the
/// Push API and the gateway only stores them. The endpoint identifies the subscription to its
/// push service and is treated as the primary key - a browser that re-subscribes gets a new
/// endpoint, and the old one starts returning 410 Gone.
/// </remarks>
public sealed record PushSubscription
{
    /// <summary>The push service URL to POST to. Identifies the subscription.</summary>
    public required string Endpoint { get; init; }

    /// <summary>The subscriber's public key, base64url. Payloads are encrypted to it.</summary>
    public required string P256dh { get; init; }

    /// <summary>The subscriber's auth secret, base64url.</summary>
    public required string Auth { get; init; }

    /// <summary>What subscribed, when the browser volunteered it. For diagnosis only.</summary>
    public string? UserAgent { get; init; }

    /// <summary>When it was first stored.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When the push service last accepted a message for it, or null if never.</summary>
    public DateTimeOffset? LastSuccessAtUtc { get; init; }
}

/// <summary>Where push subscriptions are kept.</summary>
public interface IPushSubscriptionStore
{
    /// <summary>Creates the schema.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores a subscription, replacing any earlier one with the same endpoint.
    /// </summary>
    /// <remarks>
    /// Upsert rather than insert: a browser re-subscribes on its own schedule - after a permission
    /// change, a service worker update, or a key rotation - and often hands back an endpoint it
    /// already has. Failing that would drop notifications for a device that just told us it wants
    /// them.
    /// </remarks>
    Task SaveAsync(PushSubscription subscription, CancellationToken ct = default);

    /// <summary>Every subscription, for a broadcast.</summary>
    Task<IReadOnlyList<PushSubscription>> ListAsync(CancellationToken ct = default);

    /// <summary>Removes one by endpoint. Returns whether it existed.</summary>
    Task<bool> RemoveAsync(string endpoint, CancellationToken ct = default);

    /// <summary>Records that the push service accepted a message.</summary>
    Task MarkDeliveredAsync(string endpoint, CancellationToken ct = default);
}
