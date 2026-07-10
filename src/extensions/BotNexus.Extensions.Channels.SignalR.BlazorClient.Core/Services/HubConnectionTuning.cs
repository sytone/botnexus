using Microsoft.AspNetCore.SignalR.Client;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Optional per-connection SignalR tuning applied when a client builds its hub connection (#1840).
/// </summary>
/// <remarks>
/// The desktop portal passes <see langword="null"/> and keeps the framework's stock timeouts and
/// the default automatic-reconnect budget, so its behaviour is unchanged. The mobile client passes
/// a populated instance so a connection tunnelled through netbird to a backgrounded PWA gets a
/// longer server timeout, a keep-alive cadence that holds the tunnel's idle window open, and a
/// widened reconnect schedule. Keeping the tuning in an optional value object means the shared
/// <see cref="GatewayHubConnection.ConnectAsync"/> build path is not forked per client kind.
///
/// <para>
/// <see cref="ServerTimeout"/> is the client-side counterpart to the server's
/// <c>ClientTimeoutInterval</c>: it must be at least twice <see cref="KeepAliveInterval"/> so a
/// single missed server ping does not trip a client-side timeout. A null field leaves the
/// corresponding <see cref="HubConnection"/> property at its framework default.
/// </para>
/// </remarks>
public sealed class HubConnectionTuning
{
    /// <summary>
    /// How long the client waits for a server message/ping before treating the connection as dead
    /// (maps to <see cref="HubConnection.ServerTimeout"/>). Framework default is 30s; the mobile
    /// client widens this. Null leaves the framework default in place.
    /// </summary>
    public TimeSpan? ServerTimeout { get; init; }

    /// <summary>
    /// How often the client sends a keep-alive ping to the server (maps to
    /// <see cref="HubConnection.KeepAliveInterval"/>). Framework default is 15s. Chosen on mobile
    /// to sit under the netbird tunnel idle-cutoff. Null leaves the framework default in place.
    /// </summary>
    public TimeSpan? KeepAliveInterval { get; init; }

    /// <summary>
    /// Optional automatic-reconnect retry policy. When null, the client uses the framework default
    /// <c>WithAutomaticReconnect()</c> budget (~5 retries x 3s). The mobile client supplies a
    /// widened, indefinitely-retrying policy so a returning backgrounded app self-heals.
    /// </summary>
    public IRetryPolicy? ReconnectRetryPolicy { get; init; }
}
