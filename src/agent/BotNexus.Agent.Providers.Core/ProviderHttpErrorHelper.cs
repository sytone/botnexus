using System.Net;
using System.Net.Http.Headers;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Abstractions.Text;

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
    /// Maximum number of BYTES of an untrusted error body that will ever be pulled off the wire
    /// (64 KiB). Far smaller than <see cref="BoundedHttpContent.DefaultMaxResponseBytes"/> because an
    /// error body is diagnostic context, not a payload: nothing downstream parses it, so there is no
    /// legitimate reason to buffer megabytes of it. This is the availability half of #3399.
    /// </summary>
    public const long MaxErrorBodyBytes = 64L * 1024;

    /// <summary>
    /// Maximum number of CHARACTERS of redacted error detail that reaches an exception message
    /// (2,048). The byte cap bounds what is read; this bounds what is <em>rendered</em>, because the
    /// message is persisted and shown to a user. Sized to keep a typical JSON error object intact.
    /// </summary>
    public const int MaxErrorDetailChars = 2048;

    /// <summary>
    /// Appended when, and only when, the detail was actually shortened - so the marker is evidence of
    /// a cut rather than decoration on every message.
    /// </summary>
    public const string TruncationMarker = "... [truncated]";

    /// <summary>
    /// Substituted when the error body could not be read at all. A read failure on the error path
    /// must not replace the real status-code diagnosis with a transport exception that hides it.
    /// </summary>
    public const string UnreadableBodyMarker = "[body unavailable]";

    /// <summary>
    /// Reads a bounded prefix of an untrusted HTTP error body, redacts it, and truncates the result to
    /// <see cref="MaxErrorDetailChars"/> - the single seam any caller building an exception message
    /// out of a remote error response should use (#3399).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Order matters: redact, then truncate.</b> Truncating first would let a secret sitting past
    /// the character bound escape the redactor and then merely be cut in half, and a half-credential
    /// in a persisted message is still a disclosure.
    /// </para>
    /// <para>
    /// <b>A failed read is not a failure.</b> Every caller is already on its error path. If the body
    /// cannot be read - a faulted stream, a stalled peer, an oversized frame - this returns
    /// <see cref="UnreadableBodyMarker"/> rather than throwing, so the status code and reason phrase
    /// still reach the user. Caller cancellation is deliberately NOT swallowed: a cancelled turn must
    /// surface as cancellation, not as a missing body.
    /// </para>
    /// </remarks>
    /// <param name="response">The failed response whose body is to be summarised.</param>
    /// <param name="secretRedactor">
    /// Optional redactor. <see langword="null"/> is a deliberate no-op rather than a blanket drop, so
    /// an un-wired caller keeps its diagnostics instead of silently losing them.
    /// </param>
    /// <param name="maxBytes">Wire cap. Defaults to <see cref="MaxErrorBodyBytes"/>.</param>
    /// <param name="maxChars">Render cap. Defaults to <see cref="MaxErrorDetailChars"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Bounded, redacted error detail safe to interpolate into an exception message.</returns>
    public static async Task<string> ReadBoundedRedactedErrorDetailAsync(
        HttpResponseMessage response,
        ISecretRedactor? secretRedactor,
        long maxBytes = MaxErrorBodyBytes,
        int maxChars = MaxErrorDetailChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        string raw;
        bool truncatedOnWire;
        try
        {
            (raw, truncatedOnWire) = await BoundedHttpContent
                .ReadStringPrefixAsync(response.Content, maxBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Intentionally broad: the body is best-effort diagnostics, and every failure mode here
            // (IO, stall, protocol, decoding) has the same correct response - keep the status-code
            // diagnosis and say the body was unavailable.
            return UnreadableBodyMarker;
        }

        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        var redacted = Redact(raw, secretRedactor);
        var clipped = redacted.Length > maxChars
            ? GraphemeSafeTruncation.Truncate(redacted, maxChars, TruncationMarker)!
            : redacted;

        // A wire-level cut is itself information: without the marker the reader cannot tell a complete
        // small body from the first 64 KiB of a huge one.
        return truncatedOnWire && !clipped.EndsWith(TruncationMarker, StringComparison.Ordinal)
            ? clipped + TruncationMarker
            : clipped;
    }

    /// <summary>
    /// Redacts a short untrusted diagnostic string - a <c>ReasonPhrase</c>, a header value, a status
    /// line - before interpolation. Exposed alongside
    /// <see cref="ReadBoundedRedactedErrorDetailAsync"/> so a caller does not have to reach for the
    /// redactor directly and accidentally scrub only the body while leaving the reason phrase raw:
    /// a remote that reflects a credential into its error page can just as easily put it in the
    /// reason phrase. Null/empty input and a null redactor are pass-through.
    /// </summary>
    public static string RedactDiagnosticText(string? text, ISecretRedactor? secretRedactor)
        => string.IsNullOrEmpty(text) ? string.Empty : Redact(text, secretRedactor);

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
            // errorBody is ALREADY redacted above, so pass no redactor: BuildMessage keeps its own
            // redaction for callers that reach it directly, but redacting twice here would make the
            // cost scale with the number of branches for no additional protection.
            throw new ProviderAuthenticationException(
                ProviderAuthenticationException.BuildMessage(providerName, statusCode, errorBody),
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
