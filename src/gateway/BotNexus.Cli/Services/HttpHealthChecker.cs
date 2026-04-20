using Microsoft.Extensions.Logging;

namespace BotNexus.Cli.Services;

/// <summary>
/// HTTP-based health checker that polls an endpoint with exponential backoff.
/// </summary>
public sealed class HttpHealthChecker : IHealthChecker
{
    private readonly ILogger<HttpHealthChecker> _logger;
    private readonly HttpClient _httpClient;

    public HttpHealthChecker(ILogger<HttpHealthChecker> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
    }

    /// <summary>
    /// Polls the health endpoint with exponential backoff (200ms, 400ms, 800ms, 1600ms, 2000ms max)
    /// until a 2xx response is received or the timeout elapses.
    /// </summary>
    public async Task<bool> WaitForHealthyAsync(string healthUrl, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting health check for {HealthUrl} (timeout: {Timeout}s)", healthUrl, timeout.TotalSeconds);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var delayMs = 200;
        const int maxDelayMs = 2000;

        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                var response = await _httpClient.GetAsync(healthUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Health check succeeded for {HealthUrl} after {Elapsed}ms", healthUrl, stopwatch.ElapsedMilliseconds);
                    return true;
                }

                _logger.LogDebug("Health check returned {StatusCode}, retrying...", response.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug("Health check request failed: {Message}", ex.Message);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // HTTP client timeout - service not ready yet
                _logger.LogDebug("Health check timed out (service not responding), retrying...");
            }

            // Exponential backoff with cap
            var remainingTime = timeout - stopwatch.Elapsed;
            if (remainingTime <= TimeSpan.Zero)
                break;

            var actualDelay = TimeSpan.FromMilliseconds(Math.Min(delayMs, (int)remainingTime.TotalMilliseconds));
            await Task.Delay(actualDelay, cancellationToken);

            delayMs = Math.Min(delayMs * 2, maxDelayMs);
        }

        _logger.LogWarning("Health check failed for {HealthUrl} after {Elapsed}ms (timeout exceeded)", healthUrl, stopwatch.ElapsedMilliseconds);
        return false;
    }
}
