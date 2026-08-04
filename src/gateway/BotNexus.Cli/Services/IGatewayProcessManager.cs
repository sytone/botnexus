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

    /// <summary>
    /// Stops the running gateway process by sending a hard kill signal.
    /// Waits up to 5 seconds for the process to exit, then deletes the PID file.
    /// </summary>
    /// <param name="homePath">BotNexus home directory containing the PID file. Defaults to ~/.botnexus.</param>
    /// <param name="gatewayBinaryPath">Optional path of the gateway assembly this deployment would
    /// launch. When supplied, a missing or stale PID file falls back to discovering a live process
    /// whose executable path matches it (issue #2772). When null, only the PID file is consulted.</param>
    /// <param name="cancellationToken">Cancellation token for stop timeout.</param>
    /// <returns>
    /// A result carrying both whether the gateway is now down and what was actually observed
    /// (<see cref="GatewayStopOutcome"/>).
    /// </returns>
    Task<GatewayStopResult> StopAsync(
        string? homePath = null,
        string? gatewayBinaryPath = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the current status of the gateway process by reading the PID file
    /// and checking if the process is alive.
    /// </summary>
    /// <param name="homePath">BotNexus home directory containing the PID file. Defaults to ~/.botnexus.</param>
    /// <param name="cancellationToken">Cancellation token for status query.</param>
    /// <returns>
    /// The current gateway status, including state, PID, and uptime if running.
    /// </returns>
    Task<GatewayStatus> GetStatusAsync(string? homePath = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously checks whether the gateway process is currently running.
    /// </summary>
    /// <param name="homePath">BotNexus home directory containing the PID file. Defaults to ~/.botnexus.</param>
    /// <param name="gatewayBinaryPath">Optional gateway assembly path; enables the same
    /// discovery-by-executable-path fallback <see cref="StopAsync"/> uses when the PID file is
    /// missing or unverifiable (issue #2772).</param>
    bool IsRunning(string? homePath = null, string? gatewayBinaryPath = null);
}
