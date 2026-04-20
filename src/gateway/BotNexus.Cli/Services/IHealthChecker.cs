namespace BotNexus.Cli.Services;

/// <summary>
/// Polls a health endpoint to verify that a service is responsive.
/// </summary>
public interface IHealthChecker
{
    /// <summary>
    /// Polls the health endpoint with exponential backoff until the service
    /// returns a 2xx response or the timeout elapses.
    /// </summary>
    /// <param name="healthUrl">URL of the health endpoint to poll.</param>
    /// <param name="timeout">Maximum time to wait for the service to become healthy.</param>
    /// <param name="cancellationToken">Cancellation token to abort the health check early.</param>
    /// <returns>
    /// True if the service became healthy within the timeout; false otherwise.
    /// </returns>
    Task<bool> WaitForHealthyAsync(string healthUrl, TimeSpan timeout, CancellationToken cancellationToken = default);
}
