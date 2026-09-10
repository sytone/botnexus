namespace BotNexus.Gateway.Notifications.Push;

/// <summary>
/// What the gateway needs to talk to Apple Push Notification service.
/// </summary>
/// <remarks>
/// Token-based authentication rather than certificates: one .p8 key signs for every app under a
/// team and does not expire, where a certificate is per-app and lapses annually - which is how push
/// silently stops working on an anniversary nobody recorded.
/// <para>
/// All of this comes from an Apple Developer account, so a gateway without one simply has APNs
/// switched off. That is the normal case and must stay silent: an operator with no iOS app should
/// never see a warning about one.
/// </para>
/// </remarks>
public sealed class ApnsOptions
{
    /// <summary>Apple Developer team identifier, ten characters.</summary>
    public string? TeamId { get; init; }

    /// <summary>Identifier of the .p8 signing key, ten characters.</summary>
    public string? KeyId { get; init; }

    /// <summary>The app's bundle identifier, sent as the APNs topic.</summary>
    public string? BundleId { get; init; }

    /// <summary>Path to the .p8 private key file downloaded from Apple.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>
    /// Whether every part needed to sign and address a push is present. Nothing is attempted
    /// unless this holds, so an unconfigured gateway does no work and reports no error.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(TeamId)
        && !string.IsNullOrWhiteSpace(KeyId)
        && !string.IsNullOrWhiteSpace(BundleId)
        && !string.IsNullOrWhiteSpace(PrivateKeyPath);

    /// <summary>Reads the options from configuration under <c>gateway:apns</c>.</summary>
    public static ApnsOptions FromConfiguration(Func<string, string?> read) => new()
    {
        TeamId = read("gateway:apns:teamId"),
        KeyId = read("gateway:apns:keyId"),
        BundleId = read("gateway:apns:bundleId"),
        PrivateKeyPath = read("gateway:apns:privateKeyPath"),
    };
}

/// <summary>Which Apple push environment a device token belongs to.</summary>
/// <remarks>
/// Not interchangeable, and the failure is confusing: a token minted by a development build is
/// rejected by the production host with BadDeviceToken, which reads like a bad token rather than
/// like the wrong address. The registering client declares which it is.
/// </remarks>
public static class ApnsEnvironment
{
    /// <summary>Development builds and TestFlight installs from Xcode.</summary>
    public const string Sandbox = "sandbox";

    /// <summary>App Store and TestFlight builds.</summary>
    public const string Production = "production";

    /// <summary>The APNs host for an environment, defaulting to production.</summary>
    public static string HostFor(this string? environment) =>
        string.Equals(environment, Sandbox, StringComparison.OrdinalIgnoreCase)
            ? "https://api.sandbox.push.apple.com"
            : "https://api.push.apple.com";

    /// <summary>Normalises a client-supplied value, rejecting anything unrecognised.</summary>
    public static string? Normalise(this string? environment) => environment?.ToLowerInvariant() switch
    {
        Sandbox => Sandbox,
        Production => Production,
        _ => null,
    };
}
