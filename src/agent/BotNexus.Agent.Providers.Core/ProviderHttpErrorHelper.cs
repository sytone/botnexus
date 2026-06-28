using System.Net;
using System.Net.Http.Headers;

namespace BotNexus.Agent.Providers.Core;

/// <summary>
/// Shared utility for provider HTTP error handling.
/// Converts rate-limit responses into <see cref="ProviderRateLimitException"/> with parsed Retry-After,
/// and authentication failures (401/403) into <see cref="ProviderAuthenticationException"/> with an
/// actionable, user-facing message.
/// </summary>
public static class ProviderHttpErrorHelper
{
    /// <summary>
    /// Throws the appropriate exception for a failed HTTP response.
    /// For 429 responses, throws <see cref="ProviderRateLimitException"/> with the parsed Retry-After delay.
    /// For 401/403 responses, throws <see cref="ProviderAuthenticationException"/> with an actionable
    /// message so the failure self-diagnoses (rotate the key or switch models) instead of falling
    /// through as a generic, undiagnosable stream error and silently walking the model fallback ladder.
    /// For all other failures, throws <see cref="HttpRequestException"/> with the status code and body.
    /// </summary>
    public static void ThrowForFailedResponse(
        HttpResponseMessage response,
        string errorBody,
        string providerName)
    {
        var statusCode = (int)response.StatusCode;

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = ParseRetryAfterHeader(response.Headers);
            throw new ProviderRateLimitException(
                $"{providerName} returned {statusCode}: {errorBody}",
                statusCode,
                retryAfter);
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new ProviderAuthenticationException(
                ProviderAuthenticationException.BuildMessage(providerName, statusCode, errorBody),
                statusCode,
                providerName);
        }

        throw new HttpRequestException($"HTTP {statusCode}: {errorBody}");
    }

    private static TimeSpan? ParseRetryAfterHeader(HttpResponseHeaders headers)
    {
        if (headers.RetryAfter is null)
            return null;

        // RetryConditionHeaderValue has either Delta (TimeSpan) or Date
        if (headers.RetryAfter.Delta is { } delta && delta > TimeSpan.Zero)
            return delta <= TimeSpan.FromMinutes(2) ? delta : TimeSpan.FromMinutes(2);

        if (headers.RetryAfter.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero && delay <= TimeSpan.FromMinutes(2))
                return delay;
        }

        return null;
    }
}
