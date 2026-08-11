using System.Net;
using System.Net.Http.Headers;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// Regression tests for #2881: a provider error body must pass through <see cref="ISecretRedactor"/>
/// before it is interpolated into an exception message.
///
/// <para>
/// <b>Why this matters more than a normal logging leak.</b> The message these helpers build is not
/// merely logged. <c>Agent.cs</c> copies <c>ex.Message</c> straight into the assistant message's
/// <c>ErrorMessage</c> and into the session state, so it is <b>persisted</b> and <b>rendered to the
/// user</b>. Several providers echo the rejected <c>Authorization</c> header or API key back in a
/// 401/403 body, which is precisely the response that produces the most detailed message. Redaction
/// therefore has to happen at the single choke point, before any string interpolation, rather than
/// at the five provider call sites where a newly added sixth would silently miss it.
/// </para>
///
/// <para>
/// Every token in this file is <b>synthetic</b> and matches no real credential.
/// </para>
/// </summary>
public sealed class ProviderErrorBodyRedactionTests
{
    /// <summary>
    /// Obviously-fake OpenAI-shaped project key. Never a real credential; it exists only to prove
    /// the string does not survive into the exception message.
    /// </summary>
    private const string SyntheticToken = "sk-live-FAKE0000000000000000000000000000TESTONLY";

    private const string Placeholder = "[REDACTED]";

    /// <summary>
    /// A minimal redactor that replaces the synthetic token. Deliberately a hand-rolled test double
    /// rather than the gateway's <c>SecretRedactor</c>: this project sits below the gateway in the
    /// dependency graph, and the assertion under test is "the seam is invoked before interpolation",
    /// not "the gateway's regex set is correct" (that is pinned by
    /// <c>SecretRedactionFenceArchitectureTests</c>).
    /// </summary>
    private sealed class StubRedactor : ISecretRedactor
    {
        public int RedactCallCount { get; private set; }

        public string Redact(string input)
        {
            RedactCallCount++;
            return input.Replace(SyntheticToken, Placeholder, StringComparison.Ordinal);
        }

        public string RedactForExternalDelivery(string input) => Redact(input);
    }

    // ---- AC1: ThrowForFailedResponse redacts before interpolation, on every branch ----

    [Fact]
    public void ThrowForFailedResponse_401WithSecretInBody_RedactsTokenFromMessage()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var redactor = new StubRedactor();

        var ex = Assert.Throws<ProviderAuthenticationException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(
                response,
                $"{{\"error\":\"invalid api key: {SyntheticToken}\"}}",
                "TestProvider",
                redactor));

        Assert.DoesNotContain(SyntheticToken, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Placeholder, ex.Message, StringComparison.Ordinal);
        // The non-secret diagnostic context must survive - redaction must not blank the body.
        Assert.Contains("invalid api key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowForFailedResponse_429WithSecretInBody_RedactsTokenFromMessage()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));

        var ex = Assert.Throws<ProviderRateLimitException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(
                response,
                $"rate limited for key {SyntheticToken}",
                "TestProvider",
                new StubRedactor()));

        Assert.DoesNotContain(SyntheticToken, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Placeholder, ex.Message, StringComparison.Ordinal);
        // Redaction must not disturb the rate-limit contract the retry ladder depends on.
        Assert.Equal(TimeSpan.FromSeconds(5), ex.RetryAfter);
    }

    [Fact]
    public void ThrowForFailedResponse_500WithSecretInBody_RedactsTokenFromMessage()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var ex = Assert.Throws<HttpRequestException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(
                response,
                $"upstream failure, token={SyntheticToken}",
                "TestProvider",
                new StubRedactor()));

        Assert.DoesNotContain(SyntheticToken, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Placeholder, ex.Message, StringComparison.Ordinal);
        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrowForFailedResponse_RedactsExactlyOnce_NotPerInterpolation()
    {
        // Pins the "single choke point" design (#2881): the auth branch delegates to BuildMessage,
        // and a naive fix would redact in both places, making the cost scale with the number of
        // branches. One call proves the body is scrubbed before it is ever interpolated.
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var redactor = new StubRedactor();

        Assert.Throws<ProviderAuthenticationException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(
                response, $"key {SyntheticToken}", "TestProvider", redactor));

        Assert.Equal(1, redactor.RedactCallCount);
    }

    // ---- AC2: BuildMessage redacts the "Provider response:" suffix ----

    [Fact]
    public void BuildMessage_WithSecretInBody_RedactsProviderResponseSuffix()
    {
        var message = ProviderAuthenticationException.BuildMessage(
            "TestProvider", 401, $"echoed credential {SyntheticToken}", new StubRedactor());

        Assert.DoesNotContain(SyntheticToken, message, StringComparison.Ordinal);
        Assert.Contains("Provider response:", message, StringComparison.Ordinal);
        Assert.Contains(Placeholder, message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_CalledDirectlyWithRedactor_CannotBypassTheChokePoint()
    {
        // BuildMessage is public and reachable without going through ThrowForFailedResponse, so it
        // carries its own redaction rather than trusting its caller to have already scrubbed.
        var direct = ProviderAuthenticationException.BuildMessage(
            "TestProvider", 403, SyntheticToken, new StubRedactor());

        Assert.DoesNotContain(SyntheticToken, direct, StringComparison.Ordinal);
    }

    // ---- AC4: a null redactor is a no-op, not a silent drop of diagnostics ----

    [Fact]
    public void ThrowForFailedResponse_NullRedactor_LeavesMessageUnchanged()
    {
        using var withRedactor = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        using var withoutRedactor = new HttpResponseMessage(HttpStatusCode.InternalServerError);
        const string body = "plain upstream failure detail";

        var explicitNull = Assert.Throws<HttpRequestException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(withRedactor, body, "TestProvider", null));
        var omitted = Assert.Throws<HttpRequestException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(withoutRedactor, body, "TestProvider"));

        // The whole point of defaulting to null rather than to a scrub-everything policy: an
        // un-wired caller keeps its diagnostics verbatim instead of losing them silently.
        Assert.Contains(body, explicitNull.Message, StringComparison.Ordinal);
        Assert.Equal(omitted.Message, explicitNull.Message);
    }

    [Fact]
    public void ThrowForFailedResponse_NullRedactor_DoesNotRedactSecretShapedText()
    {
        // Non-vacuity for the no-op path: with no redactor the token is NOT removed. If this ever
        // fails, some other layer is scrubbing and the redactor tests above would pass vacuously.
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var ex = Assert.Throws<HttpRequestException>(() =>
            ProviderHttpErrorHelper.ThrowForFailedResponse(
                response, $"token={SyntheticToken}", "TestProvider", null));

        Assert.Contains(SyntheticToken, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMessage_NullRedactor_LeavesBodyUnchanged()
    {
        const string body = "plain diagnostic detail";

        var explicitNull = ProviderAuthenticationException.BuildMessage("TestProvider", 401, body, null);
        var omitted = ProviderAuthenticationException.BuildMessage("TestProvider", 401, body);

        Assert.Contains(body, explicitNull, StringComparison.Ordinal);
        Assert.Equal(omitted, explicitNull);
    }

    [Fact]
    public void BuildMessage_EmptyBodyWithRedactor_StillOmitsProviderResponseSuffix()
    {
        // Redaction must not turn an empty body into a stray "Provider response: " tail.
        var message = ProviderAuthenticationException.BuildMessage(
            "TestProvider", 401, string.Empty, new StubRedactor());

        Assert.DoesNotContain("Provider response:", message, StringComparison.Ordinal);
        Assert.Contains("TestProvider", message, StringComparison.Ordinal);
    }
}
