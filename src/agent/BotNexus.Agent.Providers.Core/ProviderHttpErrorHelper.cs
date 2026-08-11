using System.Net;
using System.Net.Http.Headers;
using BotNexus.Gateway.Abstractions.Security;

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
    /// <param name="response">The failed provider response.</param>
    /// <param name="errorBody">
    /// The raw provider error body. It is <b>untrusted credential-bearing text</b>: several providers
    /// echo the offending <c>Authorization</c> header or API key back on a 401/403, and this string is
    /// interpolated into an exception message that <c>Agent.cs</c> persists verbatim as the
    /// session-visible <c>ErrorMessage</c>. It is therefore redacted here, at the single choke point,
    /// rather than at each of the five call sites (#2881).
    /// </param>
    /// <param name="providerName">The provider that failed, named in the message.</param>
    /// <param name="secretRedactor">
    /// Optional secret redactor. When supplied, <paramref name="errorBody"/> is passed through it
    /// before ANY string interpolation. When <see langword="null"/> the body is used unchanged --
    /// deliberately a no-op rather than a blanket drop, so a caller that has not yet been wired does
    /// not silently lose its diagnostics, and so no existing caller breaks.
    /// </param>
    public static void ThrowForFailedResponse(
        HttpResponseMessage response,
        string errorBody,
        string providerName,
        ISecretRedactor? secretRedactor = null)
    {
        var statusCode = (int)response.StatusCode;

        // Redact ONCE, before any interpolation. Every throw below consumes the redacted value, so a
        // new branch added here cannot reintroduce the leak by reaching for the raw parameter.
        errorBody = Redact(errorBody, secretRedactor);

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
                ProviderAuthenticationException.BuildMessage(providerName, statusCode, errorBody, secretRedactor),
                statusCode,
                providerName);
        }

        throw new HttpRequestException($"HTTP {statusCode}: {errorBody}");
    }

    /// <summary>
    /// Applies the redactor to untrusted provider text. Shared by this helper and
    /// <see cref="ProviderAuthenticationException.BuildMessage"/> so both surfaces cannot drift.
    /// Null redactor and null/empty input are pass-through.
    /// </summary>
    internal static string Redact(string errorBody, ISecretRedactor? secretRedactor)
        => secretRedactor is null || string.IsNullOrEmpty(errorBody)
            ? errorBody
            : secretRedactor.Redact(errorBody);

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
