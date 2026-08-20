using System.Reflection;
using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Providers.Core;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Coverage for the loop-level transient-error classifier (issue #2856).
/// </summary>
/// <remarks>
/// The classifier decides whether a provider failure that surfaces as exception text is retried or
/// hard-failed to the user. The previous <c>Contains</c> chain matched eight substrings and missed
/// most of the vocabulary providers actually emit, while over-matching the bare word <c>timeout</c>.
/// These tests pin both directions: the expanded transient vocabulary, the full legacy set as a
/// regression guard, and the non-provider "timeout" sentence that must NOT trigger a retry.
/// </remarks>
public class TransientErrorClassifierTests
{
    /// <summary>Provider failure texts that OpenCode retries and BotNexus previously did not.</summary>
    [Theory]
    [InlineData("overloaded")]
    [InlineData("Internal server error")]
    [InlineData("internal_error")]
    [InlineData("server_error")]
    [InlineData("provider returned error")]
    [InlineData("fetch failed")]
    [InlineData("failed to fetch")]
    [InlineData("terminated")]
    [InlineData("ECONNRESET")]
    [InlineData("ECONNREFUSED")]
    [InlineData("ETIMEDOUT")]
    [InlineData("EAI_AGAIN")]
    [InlineData("socket hang up")]
    [InlineData("connection reset before headers")]
    [InlineData("getaddrinfo ENOTFOUND api.example.com")]
    [InlineData("resource exhausted")]
    [InlineData("resource_exhausted")]
    [InlineData("HTTP 500")]
    [InlineData("HTTP 524")]
    public void IsTransient_ExpandedProviderVocabulary_ReturnsTrue(string message)
        => Assert.True(TransientErrorClassifier.IsTransient(message), message);

    /// <summary>
    /// Regression guard: every string the legacy substring chain matched must still classify as
    /// transient. All eight original patterns are enumerated explicitly.
    /// </summary>
    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("Too Many Requests")]
    [InlineData("temporarily unavailable")]
    [InlineData("Service Unavailable")]
    [InlineData("HTTP 429")]
    [InlineData("HTTP 502")]
    [InlineData("HTTP 503")]
    [InlineData("HTTP 504")]
    public void IsTransient_LegacyPatterns_StillReturnTrue(string message)
        => Assert.True(TransientErrorClassifier.IsTransient(message), message);

    /// <summary>The timeout family the loop must keep retrying, now matched by anchored patterns.</summary>
    [Theory]
    [InlineData("timeout")]
    [InlineData("request timed out")]
    [InlineData("request timeout")]
    [InlineData("connection timed out")]
    [InlineData("stream timeout")]
    public void IsTransient_ProviderTimeouts_ReturnTrue(string message)
        => Assert.True(TransientErrorClassifier.IsTransient(message), message);

    /// <summary>
    /// Sad path: a tool message merely mentioning the word "timeout" is not a provider transient
    /// failure and must surface to the user rather than burn the retry budget.
    /// </summary>
    [Theory]
    [InlineData("the exec tool timeout setting is 120s")]
    [InlineData("invalid api key")]
    [InlineData("model not found")]
    [InlineData("tool 'shell' has no timeout configured")]
    [InlineData("")]
    [InlineData("   ")]
    public void IsTransient_NonTransientMessages_ReturnFalse(string message)
        => Assert.False(TransientErrorClassifier.IsTransient(message), message);

    /// <summary>
    /// Natural-language capacity prose (#3472): providers that surface capacity pressure as text in
    /// an otherwise successful stream must enter the retry lane, not kill the turn.
    /// </summary>
    [Theory]
    [InlineData("The model is currently at capacity. Please try again later.")]
    [InlineData("Service is temporarily at capacity")]
    [InlineData("Please try again in 30 seconds")]
    public void IsTransient_CapacityProse_ReturnsTrue(string message)
        => Assert.True(TransientErrorClassifier.IsTransient(message), message);

    /// <summary>
    /// Near-miss guard (#3472): the bare word "again" is not sufficient. "try again with a shorter
    /// prompt" is a terminal instruction to the caller, not a capacity signal.
    /// </summary>
    [Theory]
    [InlineData("Invalid request; try again with a shorter prompt")]
    [InlineData("try again with a different model")]
    [InlineData("the server is at capacity planning stage")]
    public void IsTransient_CapacityProseNearMisses_ReturnFalse(string message)
        => Assert.False(TransientErrorClassifier.IsTransient(message), message);

    /// <summary>
    /// Capacity prose lands in the transient lane, not the exhaustion lane -- the #3015 ordering
    /// contract (transient table consulted before exhaustion) is what guarantees this.
    /// </summary>
    [Theory]
    [InlineData("The model is currently at capacity. Please try again later.")]
    [InlineData("Service is temporarily at capacity")]
    [InlineData("Please try again in 30 seconds")]
    public void Classify_CapacityProse_ReturnsTransient(string message)
        => Assert.Equal(ProviderFailureClass.Transient, TransientErrorClassifier.Classify(message));

    /// <summary>
    /// Ordering regression (#3015 + #3472): a quota message that ALSO says "try again later" keeps
    /// its historical transient lane, because the transient table is consulted first.
    /// </summary>
    [Fact]
    public void Classify_QuotaTextWithCapacityProse_StaysTransient()
        => Assert.Equal(
            ProviderFailureClass.Transient,
            TransientErrorClassifier.Classify("You exceeded your current quota. Please try again later."));

    /// <summary>Exhaustion text without capacity prose is unaffected by the new patterns.</summary>
    [Theory]
    [InlineData("billing has been disabled")]
    [InlineData("insufficient_quota")]
    [InlineData("credit balance is too low")]
    public void Classify_ExhaustionText_StillReturnsExhausted(string message)
        => Assert.Equal(ProviderFailureClass.Exhausted, TransientErrorClassifier.Classify(message));

    /// <summary>A null message or exception is not transient.</summary>
    [Fact]
    public void IsTransient_Null_ReturnsFalse()
    {
        Assert.False(TransientErrorClassifier.IsTransient((string?)null));
        Assert.False(TransientErrorClassifier.IsTransient((Exception?)null));
    }

    /// <summary>A typed rate-limit exception is transient even with unhelpful message text.</summary>
    [Fact]
    public void IsTransient_ProviderRateLimitException_ReturnsTrueRegardlessOfMessage()
        => Assert.True(TransientErrorClassifier.IsTransient(new ProviderRateLimitException("nondescript", 429, null)));

    /// <summary>A non-transient exception type with non-transient text is not retried.</summary>
    [Fact]
    public void IsTransient_UnrelatedException_ReturnsFalse()
        => Assert.False(TransientErrorClassifier.IsTransient(new InvalidOperationException("bad request: unknown model")));

    /// <summary>
    /// Acceptance criterion 4: patterns are declared once as a compiled static table, so the
    /// classifier allocates no Regex per call.
    /// </summary>
    [Fact]
    public void TransientPatterns_AreStaticReadonlyCompiledRegexTable()
    {
        var field = typeof(TransientErrorClassifier).GetField(
            "TransientPatterns",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field!.IsInitOnly, "pattern table must be readonly");
        Assert.Equal(typeof(Regex[]), field.FieldType);

        var patterns = Assert.IsType<Regex[]>(field.GetValue(null));
        Assert.NotEmpty(patterns);
        Assert.All(patterns, p => Assert.True(p.Options.HasFlag(RegexOptions.Compiled), $"{p} not compiled"));
    }
}
