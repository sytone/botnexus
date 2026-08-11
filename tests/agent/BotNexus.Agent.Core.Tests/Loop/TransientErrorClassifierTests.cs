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
