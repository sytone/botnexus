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
    /// <param name="options">Configuration for the gateway process.</param>
    /// <param name="cancellationToken">Cancellation token for startup timeout.</param>
    /// <returns>
    /// A result indicating whether the gateway started successfully and became healthy.
    /// </returns>
    Task<GatewayStartResult> StartAsync(GatewayStartOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the running gateway process by sending a hard kill signal.
    /// Waits up to 5 seconds for the process to exit, then deletes the PID file.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for stop timeout.</param>
    /// <returns>
    /// A result indicating whether the gateway was stopped successfully.
    /// </returns>
    Task<GatewayStopResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries the current status of the gateway process by reading the PID file
    /// and checking if the process is alive.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for status query.</param>
    /// <returns>
    /// The current gateway status, including state, PID, and uptime if running.
    /// </returns>
    Task<GatewayStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously checks whether the gateway process is currently running.
    /// </summary>
    bool IsRunning { get; }
}
