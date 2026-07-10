using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Mobile.Services;

/// <summary>
/// Mobile-scoped SignalR keep-alive / server-timeout configuration and the factory that turns it
/// into a shared <see cref="HubConnectionTuning"/> for the mobile hub connection (#1840).
/// </summary>
/// <remarks>
/// These values are read from the mobile client's <c>appsettings.json</c> (section
/// <c>SignalR</c>) so they are configurable per deployment rather than hard-coded, but they carry
/// sensible mobile defaults tuned against the netbird tunnel idle window (see
/// <c>docs/signalr-mobile-keepalive.md</c>). The bound values are seconds; a non-positive or
/// missing value falls back to the mobile default. This type lives in the mobile project so it
/// cannot affect the desktop client, which never constructs it.
/// </remarks>
public sealed class MobileHubTuningOptions
{
    /// <summary>
    /// Default client keep-alive ping interval, in seconds. Matches the framework's 15s so the
    /// client keeps the tunnel's idle window open at the same cadence the server pings back.
    /// </summary>
    public const int DefaultKeepAliveIntervalSeconds = 15;

    /// <summary>
    /// Default client server-timeout, in seconds. Widened from the framework's 30s so a mobile
    /// client tunnelled through netbird tolerates radio stalls and tunnel jitter without declaring
    /// the server dead. Kept at at least twice the keep-alive interval (SignalR's recommended
    /// minimum ratio) via <see cref="ToTuning"/>.
    /// </summary>
    public const int DefaultServerTimeoutSeconds = 60;

    /// <summary>Client keep-alive ping interval, in seconds. Non-positive falls back to the default.</summary>
    public int KeepAliveIntervalSeconds { get; set; } = DefaultKeepAliveIntervalSeconds;

    /// <summary>Client server-timeout, in seconds. Non-positive falls back to the default.</summary>
    public int ServerTimeoutSeconds { get; set; } = DefaultServerTimeoutSeconds;

    /// <summary>
    /// Builds the shared <see cref="HubConnectionTuning"/> for the mobile hub connection: the
    /// configured (or default) keep-alive and server timeout plus the widened, indefinitely
    /// retrying <see cref="MobileReconnectRetryPolicy"/>. The server timeout is coerced to at least
    /// twice the keep-alive interval so a single missed ping cannot trip a client-side timeout.
    /// </summary>
    /// <returns>The tuning applied to the mobile hub build.</returns>
    public HubConnectionTuning ToTuning()
    {
        var keepAlive = KeepAliveIntervalSeconds > 0
            ? KeepAliveIntervalSeconds
            : DefaultKeepAliveIntervalSeconds;
        var serverTimeout = ServerTimeoutSeconds > 0
            ? ServerTimeoutSeconds
            : DefaultServerTimeoutSeconds;
        if (serverTimeout < keepAlive * 2)
            serverTimeout = keepAlive * 2;

        return new HubConnectionTuning
        {
            KeepAliveInterval = TimeSpan.FromSeconds(keepAlive),
            ServerTimeout = TimeSpan.FromSeconds(serverTimeout),
            ReconnectRetryPolicy = new MobileReconnectRetryPolicy(),
        };
    }
}
