namespace BotNexus.Gateway.Diagnostics;

/// <summary>
/// Response model for the /health endpoint with timeout awareness.
/// </summary>
public sealed record HealthResponse(string Status, string? LastActivity, double? InactivitySeconds);

/// <summary>
/// Helper for executing the health endpoint logic with a timeout.
/// If the health handler cannot complete within the timeout, the endpoint returns a 
/// "timeout" status — indicating the process is alive but unable to schedule work.
/// </summary>
public static class HealthEndpointHelper
{
    /// <summary>
    /// Executes the health check factory with a timeout. If the cancellation token fires
    /// before the factory completes, returns a timeout response.
    /// </summary>
    /// <param name="factory">Factory that produces the health response.</param>
    /// <param name="cancellationToken">Cancellation token with timeout applied.</param>
    /// <returns>The health response, or a timeout response if execution took too long.</returns>
    public static async Task<HealthResponse> ExecuteWithTimeoutAsync(
        Func<Task<HealthResponse>> factory,
        CancellationToken cancellationToken)
    {
        try
        {
            var task = factory();
            var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cancellationToken));

            if (completed == task)
            {
                return await task;
            }

            // Timeout: cancellation fired before the factory completed
            return new HealthResponse("timeout", null, null);
        }
        catch (OperationCanceledException)
        {
            return new HealthResponse("timeout", null, null);
        }
    }
}
