using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Utilities;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Agent.Providers.Core.Tests;

/// <summary>
/// #3399: the bounded + redacted error-detail seam that every HTTP caller building an exception
/// message out of an untrusted remote body must go through.
///
/// <para>
/// Two properties are pinned here, both of which the cross-world relay adapter previously lacked:
/// the body read is <b>bounded</b> (a hostile peer cannot force an arbitrarily large string into an
/// exception message that is persisted and rendered), and the resulting text is <b>redacted</b>
/// before any interpolation (a peer that reflects the request headers back in its error page must
/// not round-trip the shared credential).
/// </para>
///
/// <para>Every token in this file is <b>synthetic</b> and matches no real credential.</para>
/// </summary>
public sealed class BoundedRedactedErrorDetailTests
{
    private const string SyntheticToken = "sk-live-FAKE0000000000000000000000000000TESTONLY";
    private const string Placeholder = "[REDACTED]";

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

    private static HttpResponseMessage ResponseWithBody(string body, string? mediaType = "text/plain")
        => new(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType ?? "text/plain")
        };

    // ---- AC1 / AC3: redaction happens before the caller ever sees the text ----

    [Fact]
    public async Task ReadBoundedRedactedErrorDetailAsync_RedactsSecretShapedBody()
    {
        using var response = ResponseWithBody($"upstream rejected: {SyntheticToken}");
        var redactor = new StubRedactor();

        var detail = await ProviderHttpErrorHelper.ReadBoundedRedactedErrorDetailAsync(
            response, redactor);

        Assert.DoesNotContain(SyntheticToken, detail, StringComparison.Ordinal);
        Assert.Contains(Placeholder, detail, StringComparison.Ordinal);
        // Diagnostics must survive redaction - the point is to scrub the credential, not the context.
        Assert.Contains("upstream rejected", detail, StringComparison.Ordinal);
        Assert.Equal(1, redactor.RedactCallCount);
    }

    [Fact]
    public async Task ReadBoundedRedactedErrorDetailAsync_NullRedactor_KeepsBodyVerbatim()
    {
        // Non-vacuity for the redactor tests: with no redactor the token survives, proving no other
        // layer is silently scrubbing and making the assertions above pass for the wrong reason.
        using var response = ResponseWithBody($"upstream rejected: {SyntheticToken}");

        var detail = await ProviderHttpErrorHelper.ReadBoundedRedactedErrorDetailAsync(
            response, null);

        Assert.Contains(SyntheticToken, detail, StringComparison.Ordinal);
    }

    // ---- AC2 / AC4: the read is bounded and the result is truncated to the documented bound ----

    [Fact]
    public async Task ReadBoundedRedactedErrorDetailAsync_OversizedBody_TruncatesToDocumentedBound()
    {
        var oversized = new string('x', ProviderHttpErrorHelper.MaxErrorDetailChars * 40);
        using var response = ResponseWithBody(oversized);

        var detail = await ProviderHttpErrorHelper.ReadBoundedRedactedErrorDetailAsync(
            response, null);

        Assert.True(
            detail.Length <= ProviderHttpErrorHelper.MaxErrorDetailChars
                + ProviderHttpErrorHelper.TruncationMarker.Length,
            $"detail length {detail.Length} exceeded the documented bound of " +
            $"{ProviderHttpErrorHelper.MaxErrorDetailChars} chars (+ marker).");
        Assert.EndsWith(ProviderHttpErrorHelper.TruncationMarker, detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadBoundedRedactedErrorDetailAsync_ShortBody_IsNotMarkedTruncated()
    {
        // The truncation marker must be evidence of an actual cut, not decoration on every message.
        using var response = ResponseWithBody("short and complete");

        var detail = await ProviderHttpErrorHelper.ReadBoundedRedactedErrorDetailAsync(
            response, null);

        Assert.Equal("short and complete", detail);
        Assert.DoesNotContain(ProviderHttpErrorHelper.TruncationMarker, detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadBoundedRedactedErrorDetailAsync_SecretBeyondTheCharBound_IsStillRedacted()
    {
        // Ordering pin: redaction MUST run before truncation. Truncating first would let a secret
        // sitting past the char bound escape the redactor and then be cut in half rather than
        // removed - and a half-secret in a persisted message is still a disclosure.
        var padding = new string('y', ProviderHttpErrorHelper.MaxErrorDetailChars + 10);
        using var response = ResponseWithBody($"{padding}{SyntheticToken}");
        var redactor = new StubRedactor();

        var detail = await ProviderHttpErrorHelper.ReadBoundedRedactedErrorDetailAsync(
            response, redactor);

        Assert.DoesNotContain(SyntheticToken, detail, StringComparison.Ordinal);
        Assert.Equal(1, redactor.RedactCallCount);
    }

    [Fact]
    public async Task ReadBoundedRedactedErrorDetailAsync_BodyBeyondByteCap_ReadsOnlyThePrefix()
    {
        // The byte cap is the availability half of #3399: the string is never materialised in full,
        // so a peer streaming megabytes cannot be buffered into an exception message.
        var huge = new string('z', (int)(ProviderHttpErrorHelper.MaxErrorBodyBytes * 3));
        using var response = ResponseWithBody(huge);

        var detail = await ProviderHttpErrorHelper.ReadBoundedRedactedErrorDetailAsync(
            response, null);

        Assert.True(detail.Length < huge.Length);
        Assert.EndsWith(ProviderHttpErrorHelper.TruncationMarker, detail, StringComparison.Ordinal);
    }

    // ---- sad paths: a body that cannot be read must not take out the error path itself ----

    [Fact]
    public async Task ReadBoundedRedactedErrorDetailAsync_EmptyBody_ReturnsEmpty()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent(string.Empty)
        };

        var detail = await ProviderHttpErrorHelper.ReadBoundedRedactedErrorDetailAsync(
            response, new StubRedactor());

        Assert.Equal(string.Empty, detail);
    }

    [Fact]
    public async Task ReadBoundedRedactedErrorDetailAsync_FaultedBody_ReturnsPlaceholderNotThrow()
    {
        // The caller is already on its failure path building an exception message. A body read that
        // itself throws must degrade to a marker, never replace the real status-code diagnosis with
        // a transport exception that hides it.
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new ThrowingContent()
        };

        var detail = await ProviderHttpErrorHelper.ReadBoundedRedactedErrorDetailAsync(
            response, null);

        Assert.Equal(ProviderHttpErrorHelper.UnreadableBodyMarker, detail);
    }

    [Fact]
    public async Task ReadBoundedRedactedErrorDetailAsync_CallerCancellation_Propagates()
    {
        // Caller cancellation is NOT a body-read failure and must not be swallowed into the
        // placeholder: a cancelled turn has to surface as cancellation.
        using var response = ResponseWithBody("body");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProviderHttpErrorHelper.ReadBoundedRedactedErrorDetailAsync(
                response, null, cancellationToken: cts.Token));
    }

    // ---- the reusable text seam (so a Matrix-side caller can scrub a ReasonPhrase too) ----

    [Fact]
    public void RedactDiagnosticText_WithRedactor_ScrubsSecret()
    {
        var scrubbed = ProviderHttpErrorHelper.RedactDiagnosticText(
            $"reason {SyntheticToken}", new StubRedactor());

        Assert.DoesNotContain(SyntheticToken, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactDiagnosticText_NullInputOrRedactor_IsPassThrough()
    {
        Assert.Equal(string.Empty, ProviderHttpErrorHelper.RedactDiagnosticText(null, new StubRedactor()));
        Assert.Equal("plain", ProviderHttpErrorHelper.RedactDiagnosticText("plain", null));
    }

    // ---- BoundedHttpContent prefix reader: truncate rather than reject ----

    [Fact]
    public async Task ReadStringPrefixAsync_UnderCap_ReturnsWholeBodyNotTruncated()
    {
        using var content = new StringContent("all of it", Encoding.UTF8);

        var (text, truncated) = await BoundedHttpContent.ReadStringPrefixAsync(
            content, maxBytes: 1024);

        Assert.Equal("all of it", text);
        Assert.False(truncated);
    }

    [Fact]
    public async Task ReadStringPrefixAsync_OverCap_ReturnsPrefixAndFlagsTruncation()
    {
        using var content = new StringContent(new string('a', 5000), Encoding.UTF8);

        var (text, truncated) = await BoundedHttpContent.ReadStringPrefixAsync(
            content, maxBytes: 100);

        Assert.True(truncated);
        Assert.Equal(100, text.Length);
    }

    [Fact]
    public async Task ReadStringPrefixAsync_NonPositiveCap_Throws()
    {
        using var content = new StringContent("x");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BoundedHttpContent.ReadStringPrefixAsync(
                content, maxBytes: 0));
    }

    private sealed class ThrowingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => throw new IOException("body read failed");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
            => throw new IOException("body read failed");
    }
}
