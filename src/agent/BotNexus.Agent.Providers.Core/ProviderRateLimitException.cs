namespace BotNexus.Agent.Providers.Core;

/// <summary>
/// Thrown when a provider returns HTTP 429 (Too Many Requests) with an optional Retry-After header.
/// The agent loop can use <see cref="RetryAfter"/> to wait the server-specified duration
/// instead of using generic exponential backoff.
/// </summary>
public sealed class ProviderRateLimitException : HttpRequestException
{
    /// <summary>
    /// The server-suggested delay before retrying, parsed from the Retry-After header.
    /// Null if the header was absent or unparseable.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    public ProviderRateLimitException(string message, int statusCode, TimeSpan? retryAfter)
        : base(message, null, (System.Net.HttpStatusCode)statusCode)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>
    /// Parses the Retry-After header value. Supports both delta-seconds and HTTP-date formats.
    /// Returns null if the value is absent, empty, or unparseable.
    /// </summary>
    public static TimeSpan? ParseRetryAfterHeader(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
            return null;

        // Try delta-seconds first (most common for rate limits)
        if (int.TryParse(headerValue.Trim(), out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(Math.Min(seconds, 120)); // Cap at 2 minutes

        // Try HTTP-date format
        if (DateTimeOffset.TryParse(headerValue.Trim(), out var date))
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero && delay <= TimeSpan.FromMinutes(2))
                return delay;
        }

        return null;
    }
}
