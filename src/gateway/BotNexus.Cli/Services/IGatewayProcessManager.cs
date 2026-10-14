namespace BotNexus.Cli.Services;

/// <summary>
/// Manages the lifecycle of the BotNexus Gateway process: start, stop, status checks.
/// </summary>
public interface IGatewayProcessManager
{
    /// <summary>
    /// Starts the gateway process according to the provided options.
    /// Returns immediately after spawning the process and performing a health check.
    /// </summary>
    /// <param name="options">Configuration for the gateway process. Set <see cref="GatewayStartOptions.HomePath"/> to control where the PID file is written.</param>
    /// <param name="cancellationToken">Cancellation token for startup timeout.</param>
    /// <returns>
    /// A result indicating whether the gateway started successfully and became healthy.
    /// </returns>
    Task<GatewayStartResult> StartAsync(GatewayStartOptions options, CancellationToken cancellationToken = default);

    /// <summary>Requests authenticated planned shutdown without requiring a PID or process handle.</summary>
    Task<bool> RequestPlannedShutdownAsync(
        string? homePath,
        string gatewayUrl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests planned shutdown through the gateway API, waits boundedly for confirmed process
    /// exit, and only then escalates through identity-verified native signalling when necessary.
    /// The PID file is retained whenever termination cannot be observed.
    /// </summary>
    /// <param name="homePath">BotNexus home directory containing the PID file. Defaults to ~/.botnexus.</param>
    /// <param name="gatewayBinaryPath">Optional path of the gateway assembly this deployment would
    /// launch. When supplied, a missing or stale PID file falls back to discovering a live process
    /// whose executable path matches it (issue #2772). When null, only the PID file is consulted.</param>
    /// <param name="cancellationToken">Cancellation token bounding the complete planned-shutdown and escalation operation.</param>
    /// <param name="gatewayUrl">Effective gateway URL used for the API-first planned-shutdown request.</param>
    /// <returns>A result carrying whether the gateway is down and what was observed.</returns>
    Task<GatewayStopResult> StopAsync(
        string? homePath = null,
        string? gatewayBinaryPath = null,
        CancellationToken cancellationToken = default,
        string? gatewayUrl = null);

    /// <summary>
    /// Queries the current status of the gateway process without mutating lifecycle state.
    /// </summary>
    /// <param name="homePath">BotNexus home directory containing the PID file. Defaults to ~/.botnexus.</param>
    /// <param name="gatewayBinaryPath">Optional gateway assembly path used to discover a live gateway when the PID file is absent or unverifiable.</param>
    /// <param name="healthUrl">Optional effective health endpoint. Defaults to the loopback endpoint.</param>
    /// <param name="cancellationToken">Cancellation token for status query.</param>
    /// <returns>
    /// The current gateway status, including state, PID, and uptime when known.
    /// </returns>
    Task<GatewayStatus> GetStatusAsync(
        string? homePath = null,
        string? gatewayBinaryPath = null,
        string? healthUrl = null,
        CancellationToken cancellationToken = default);

    /// <summary>Compatibility overload for callers that supply only home and cancellation.</summary>
    Task<GatewayStatus> GetStatusAsync(string? homePath, CancellationToken cancellationToken);

    /// <summary>
    /// Synchronously checks whether the gateway process is currently running.
    /// </summary>
    /// <param name="homePath">BotNexus home directory containing the PID file. Defaults to ~/.botnexus.</param>
    /// <param name="gatewayBinaryPath">Optional gateway assembly path; enables the same
    /// discovery-by-executable-path fallback <see cref="StopAsync"/> uses when the PID file is
    /// missing or unverifiable (issue #2772).</param>
    bool IsRunning(string? homePath = null, string? gatewayBinaryPath = null);
}
