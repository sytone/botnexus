namespace BotNexus.Satellite.Client.Core;

/// <summary>Tray-visible session states, without exposing exceptions or credential contents.</summary>
public enum ClientState
{
    /// <summary>No active connection, including after user cancellation.</summary>
    Offline,
    /// <summary>The initial connection attempt is in progress.</summary>
    Connecting,
    /// <summary>An active authenticated connection is maintained.</summary>
    Online,
    /// <summary>A transient failure or replaced connection is being recovered.</summary>
    Reconnecting,
    /// <summary>User action is required; automated retries and browser launches stop.</summary>
    NeedsAuthentication
}

/// <summary>Safe transport failure categories; arbitrary redirect bodies never reach the browser boundary.</summary>
public enum ConnectionFailure
{
    /// <summary>Network interruption eligible for bounded retry.</summary>
    Transient,
    /// <summary>Gateway or tunnel credentials must be supplied again.</summary>
    AuthenticationRequired,
    /// <summary>Credentials lack permission; requires explicit user action.</summary>
    AccessDenied,
    /// <summary>The gateway rejected a heartbeat from an older connection.</summary>
    Replaced
}

/// <summary>Transport reports a category rather than secret-bearing response bodies or redirect URLs.</summary>
public sealed class SatelliteConnectionException : Exception
{
    /// <summary>Constructs a classified failure without remote response text.</summary>
    public SatelliteConnectionException(ConnectionFailure failure) : base("Satellite connection failed") => Failure = failure;
    /// <summary>Determines whether reconnect or explicit authentication is appropriate.</summary>
    public ConnectionFailure Failure { get; }
}

/// <summary>Secret boundary DTO; never serialize, log, or put these values into settings or URLs.</summary>
public sealed class ClientCredentials
{
    /// <summary>Missing or blank means the user must provide the registered satellite API key.</summary>
    public string? GatewayApiKey { get; init; }
    /// <summary>Missing or blank on a tunnel means the user must supply a non-browser client access token.</summary>
    public string? TunnelAccessToken { get; init; }
}

/// <summary>Windows adapter must protect credentials at rest for the current user; no plaintext fallback.</summary>
public interface ICredentialStore
{
    /// <summary>Loads only from the injected protected store, honoring cancellation.</summary>
    Task<ClientCredentials> LoadAsync(CancellationToken cancellationToken);
    /// <summary>Persists explicit secret-input token without scraping browser cookies or login HTML.</summary>
    Task SaveTunnelTokenAsync(string token, CancellationToken cancellationToken);
}

/// <summary>Transport-boundary request with separate gateway and tunnel authentication headers.</summary>
public sealed class ConnectionRequest
{
    /// <summary>Dedicated /hub/satellite endpoint, never carrying credentials.</summary>
    public required Uri HubUri { get; init; }
    /// <summary>Apply to negotiation and transport requests; never forward across origin redirects.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
}

/// <summary>Portable seam for a later SignalR adapter; does not accept satellite or owner identity.</summary>
public interface ISatelliteTransport
{
    /// <summary>Connects with supplied headers; classifies authentication, transient, and replacement failures.</summary>
    Task<ISatelliteConnection> ConnectAsync(ConnectionRequest request, CancellationToken cancellationToken);
}

/// <summary>An individual live transport, owned and disposed by the session runner.</summary>
public interface ISatelliteConnection : IAsyncDisposable
{
    /// <summary>Signals active closure promptly so reconnect does not wait for a heartbeat deadline.</summary>
    Task<ConnectionFailure> Closed { get; }
    /// <summary>Invokes parameterless Heartbeat, never Register or caller-supplied identities.</summary>
    Task HeartbeatAsync(CancellationToken cancellationToken);
}

/// <summary>Explicit user-triggered handoff only; no browser cookie extraction or automatic callback required.</summary>
public interface IBrowserLauncher
{
    /// <summary>Opens only the prevalidated configured tunnel origin, never a server-supplied redirect.</summary>
    Task OpenAsync(Uri origin, CancellationToken cancellationToken);
}

/// <summary>Cancellation-aware scheduler seam; production adapter uses TimeProvider-backed delays.</summary>
public interface IClientDelay
{
    /// <summary>Completes after the duration on the supplied clock or throws on cancellation.</summary>
    Task DelayAsync(TimeSpan duration, TimeProvider clock, CancellationToken cancellationToken);
}
