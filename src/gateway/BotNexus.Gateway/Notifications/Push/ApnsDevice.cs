namespace BotNexus.Gateway.Notifications.Push;

/// <summary>One iOS device registered for notifications.</summary>
public sealed record ApnsDevice
{
    /// <summary>The APNs device token, hex. Identifies the install, not the person.</summary>
    public required string DeviceToken { get; init; }

    /// <summary>Which Apple environment minted it - sandbox or production.</summary>
    public required string Environment { get; init; }

    /// <summary>What registered, for diagnosis. Optional.</summary>
    public string? DeviceName { get; init; }

    /// <summary>When it was first stored.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When APNs last accepted a push for it, or null if never.</summary>
    public DateTimeOffset? LastSuccessAtUtc { get; init; }
}

/// <summary>Where iOS device tokens are kept.</summary>
public interface IApnsDeviceStore
{
    /// <summary>Creates the schema.</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores a device token, replacing any earlier registration of the same token.
    /// </summary>
    /// <remarks>
    /// Upsert, because iOS re-issues a token on its own schedule - after a restore, an update, or
    /// for no visible reason - and an app is expected to re-register on every launch. Rejecting a
    /// token already held would drop notifications for a device that just asked for them.
    /// </remarks>
    Task SaveAsync(ApnsDevice device, CancellationToken ct = default);

    /// <summary>Every registered device.</summary>
    Task<IReadOnlyList<ApnsDevice>> ListAsync(CancellationToken ct = default);

    /// <summary>Removes one by token. Returns whether it existed.</summary>
    Task<bool> RemoveAsync(string deviceToken, CancellationToken ct = default);

    /// <summary>Records that APNs accepted a push.</summary>
    Task MarkDeliveredAsync(string deviceToken, CancellationToken ct = default);
}
