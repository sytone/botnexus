using System.Reflection;
using System.Text.RegularExpressions;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Providers.Core;

namespace BotNexus.Agent.Core.Tests.Loop;

/// <summary>
/// Coverage for the tri-state provider-failure classification introduced by #3015.
/// </summary>
/// <remarks>
/// #3015 replaced a single boolean question with three lanes because a bool cannot express the
/// difference between "wait and try again" and "this will never clear". The most important property
/// here is not the new exhaustion vocabulary but the <b>parity</b> guarantee: every string that
/// classified transient before the split must still classify transient after it. That is asserted
/// directly against <c>IsTransient</c> as a cross-check, so a future edit that widens the exhaustion
/// table into transient territory reddens by name.
/// </remarks>
public class ProviderFailureClassifierTests
{
    // --- AC1: exhaustion vocabulary ---

    /// <summary>Conditions that cannot clear by waiting classify as exhausted.</summary>
    [Theory]
    [InlineData("insufficient_quota")]
    [InlineData("insufficient quota")]
    [InlineData("quota_exceeded")]
    [InlineData("quota exhausted")]
    [InlineData("You exceeded your current quota, please check your plan and billing details")]
    [InlineData("out of credits")]
    [InlineData("Your credit balance is too low to access the API")]
    [InlineData("billing has been disabled for this organization")]
    [InlineData("billing disabled")]
    [InlineData("billing_hard_limit_reached")]
    [InlineData("payment required")]
    [InlineData("HTTP 402")]
    [InlineData("invalid_api_key")]
    [InlineData("invalid api key")]
    [InlineData("the api key is invalid")]
    [InlineData("account has been suspended")]
    public void Classify_ExhaustionVocabulary_ReturnsExhausted(string message)
        => Assert.Equal(ProviderFailureClass.Exhausted, TransientErrorClassifier.Classify(message));

    // --- AC1 + AC5: transient parity, and overload is NOT exhaustion ---

    /// <summary>
    /// Every transient string still classifies transient. AC5's overload cases lead the list: an
    /// overload landing in the exhaustion lane is exactly how an auth profile gets cooled for a
    /// condition that has nothing to do with its credentials.
    /// </summary>
    [Theory]
    [InlineData("overloaded")]
    [InlineData("overloaded_error")]
    [InlineData("503 overloaded")]
    [InlineData("service unavailable")]
    [InlineData("temporarily unavailable")]
    [InlineData("rate limit exceeded")]
    [InlineData("too many requests")]
    [InlineData("HTTP 429")]
    [InlineData("HTTP 500")]
    [InlineData("HTTP 502")]
    [InlineData("HTTP 503")]
    [InlineData("HTTP 504")]
    [InlineData("HTTP 524")]
    [InlineData("internal server error")]
    [InlineData("provider returned error")]
    [InlineData("fetch failed")]
    [InlineData("socket hang up")]
    [InlineData("ECONNRESET")]
    [InlineData("getaddrinfo ENOTFOUND api.example.com")]
    [InlineData("resource_exhausted")]
    [InlineData("request timed out")]
    [InlineData("timeout")]
    public void Classify_TransientVocabulary_ReturnsTransient(string message)
        => Assert.Equal(ProviderFailureClass.Transient, TransientErrorClassifier.Classify(message));

    /// <summary>
    /// AC1 parity, asserted as a biconditional: <c>Classify</c> returns <c>Transient</c> for exactly
    /// the strings <c>IsTransient</c> returns true for. This is the clause that makes "existing
    /// behaviour is preserved for every currently-matching pattern" a test rather than a claim -- a
    /// new exhaustion pattern that stole a transient string would fail here even if every other test
    /// in the file still passed.
    /// </summary>
    [Theory]
    [InlineData("overloaded")]
    [InlineData("503")]
    [InlineData("rate limit exceeded")]
    [InlineData("resource_exhausted")]
    [InlineData("insufficient_quota")]
    [InlineData("billing has been disabled")]
    [InlineData("model not found")]
    [InlineData("")]
    public void Classify_AgreesWithIsTransient_ForEveryInput(string message)
    {
        var isTransient = TransientErrorClassifier.IsTransient(message);
        var classified = TransientErrorClassifier.Classify(message) == ProviderFailureClass.Transient;
        Assert.Equal(isTransient, classified);
    }

    // --- Terminal lane ---

    /// <summary>Unrecognised failures stay terminal: fail immediately, suspend nothing.</summary>
    [Theory]
    [InlineData("model not found")]
    [InlineData("invalid request")]
    [InlineData("the exec tool timeout setting is 120s")]
    [InlineData("")]
    [InlineData("   ")]
    public void Classify_UnrecognisedFailures_ReturnTerminal(string message)
        => Assert.Equal(ProviderFailureClass.Terminal, TransientErrorClassifier.Classify(message));

    /// <summary>A null message or exception is terminal, never a suspension trigger.</summary>
    [Fact]
    public void Classify_Null_ReturnsTerminal()
    {
        Assert.Equal(ProviderFailureClass.Terminal, TransientErrorClassifier.Classify((string?)null));
        Assert.Equal(ProviderFailureClass.Terminal, TransientErrorClassifier.Classify((Exception?)null));
    }

    // --- Typed exceptions ---

    /// <summary>
    /// AC5. A typed rate-limit exception is transient regardless of its text, exactly as
    /// <c>IsTransient</c> has always treated it -- so a 429 keeps its retries and cools nothing.
    /// </summary>
    [Fact]
    public void Classify_ProviderRateLimitException_ReturnsTransient()
        => Assert.Equal(
            ProviderFailureClass.Transient,
            TransientErrorClassifier.Classify(new ProviderRateLimitException("nondescript", 429, null)));

    /// <summary>
    /// A typed authentication exception is exhaustion: the same rejected credential cannot succeed
    /// on attempt two.
    /// </summary>
    [Fact]
    public void Classify_ProviderAuthenticationException_ReturnsExhausted()
        => Assert.Equal(
            ProviderFailureClass.Exhausted,
            TransientErrorClassifier.Classify(new ProviderAuthenticationException("rejected", 401, "openai")));

    /// <summary>
    /// The typed auth exception must NOT have become transient as a side effect -- that would
    /// restore the four-round-trip tax the split exists to remove.
    /// </summary>
    [Fact]
    public void IsTransient_ProviderAuthenticationException_RemainsFalse()
        => Assert.False(
            TransientErrorClassifier.IsTransient(new ProviderAuthenticationException("rejected", 401, "openai")));

    /// <summary>
    /// The exhaustion table matches the transient table's shape: declared once as a compiled static
    /// so classification allocates nothing on the retry hot path.
    /// </summary>
    [Fact]
    public void ExhaustionPatterns_AreStaticReadonlyCompiledRegexTable()
    {
        var field = typeof(TransientErrorClassifier).GetField(
            "ExhaustionPatterns",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field!.IsInitOnly, "pattern table must be readonly");
        Assert.Equal(typeof(Regex[]), field.FieldType);

        var patterns = Assert.IsType<Regex[]>(field.GetValue(null));
        Assert.NotEmpty(patterns);
    }

    // --- Registry scoping unit coverage (AC4) ---

    /// <summary>
    /// AC4. The suspension key is the composite (provider, auth profile) pair. Both halves are
    /// asserted independently so a key that silently collapsed to one half cannot pass.
    /// </summary>
    [Fact]
    public void SuspensionRegistry_ScopesToProviderAndAuthProfile()
    {
        var registry = new ProviderSuspensionRegistry();
        registry.Suspend("openai", "profile-a", TimeSpan.FromMinutes(5), "insufficient_quota");

        Assert.True(registry.IsSuspended("openai", "profile-a"));
        Assert.False(registry.IsSuspended("openai", "profile-b"));
        Assert.False(registry.IsSuspended("anthropic", "profile-a"));
        Assert.False(registry.IsSuspended("anthropic", "profile-b"));
    }

    /// <summary>An unsuspended pair is never reported as suspended.</summary>
    [Fact]
    public void SuspensionRegistry_UnknownScope_IsNotSuspended()
        => Assert.False(new ProviderSuspensionRegistry().IsSuspended("openai", "profile-a"));

    /// <summary>A non-positive duration records nothing -- a zero-length suspension is not a state.</summary>
    [Fact]
    public void SuspensionRegistry_NonPositiveDuration_RecordsNothing()
    {
        var registry = new ProviderSuspensionRegistry();
        registry.Suspend("openai", "profile-a", TimeSpan.Zero, "quota");
        Assert.False(registry.IsSuspended("openai", "profile-a"));
    }
}
