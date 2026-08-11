using System.Text.RegularExpressions;
using BotNexus.Agent.Providers.Core;

namespace BotNexus.Agent.Core.Loop;

/// <summary>
/// Classifies provider failures that surface as exception text as retryable (transient) or not.
/// </summary>
/// <remarks>
/// This is the loop-level classifier, complementing <c>TransientHttpRetryHandler</c>: the handler only
/// sees failures that reach the HTTP pipeline, whereas streaming/provider faults frequently surface to
/// the agent loop as an exception whose only usable signal is its message text.
/// <para>
/// The previous implementation was a hand-rolled <c>Contains</c> chain covering eight substrings, which
/// missed the majority of the transient vocabulary providers actually emit (overload, undici socket
/// aborts, DNS failures, <c>500</c>/<c>524</c>, resource exhaustion) and simultaneously over-matched the
/// bare word <c>timeout</c>, retrying tool-timeout text that has nothing to do with provider transience.
/// See issue #2856; ported from OpenCode <c>61aefc07</c> / <c>f929f8f1</c>.
/// </para>
/// <para>
/// Patterns are declared once as compiled statics (mirroring <c>ContextOverflowDetector</c>) so
/// classification allocates nothing per call on the retry hot path.
/// </para>
/// </remarks>
public static class TransientErrorClassifier
{
    private static readonly Regex[] TransientPatterns =
    [
        // Rate limiting / quota exhaustion.
        new("rate limit", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("too many requests", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"resource[_ ]exhausted", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Provider-side capacity and generic upstream faults.
        new("overloaded", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("temporarily unavailable", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("service unavailable", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"internal[_ ]server[_ ]error", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"internal[_ ]error", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"server[_ ]error", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("provider returned error", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Transport / stream aborts (undici and friends).
        new("fetch failed", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("failed to fetch", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bterminated\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("socket hang up", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("connection reset before headers", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bECONNRESET\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bECONNREFUSED\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bETIMEDOUT\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bEAI_AGAIN\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bENOTFOUND\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new("getaddrinfo", RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // Retryable HTTP status codes, as bare numbers in free-form provider text.
        new(@"\b(?:429|500|502|503|504|524)\b", RegexOptions.Compiled),

        // Timeouts, anchored so a tool-timeout sentence does not trigger a provider retry.
        new(@"^\s*timeout\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(
            @"\b(?:request|response|connection|network|stream|read|gateway|operation)\s+(?:timeout|timed out)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\btimed out\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>
    /// Returns <see langword="true"/> when the exception represents a retryable provider failure.
    /// </summary>
    /// <param name="exception">The exception raised while streaming a provider turn.</param>
    public static bool IsTransient(Exception? exception)
    {
        if (exception is null)
        {
            return false;
        }

        // A typed rate-limit exception is transient regardless of its message text.
        return exception is ProviderRateLimitException || IsTransient(exception.Message);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the error text matches a known transient provider failure.
    /// </summary>
    /// <param name="message">The provider error text to classify.</param>
    public static bool IsTransient(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        foreach (var pattern in TransientPatterns)
        {
            if (pattern.IsMatch(message))
            {
                return true;
            }
        }

        return false;
    }
}
