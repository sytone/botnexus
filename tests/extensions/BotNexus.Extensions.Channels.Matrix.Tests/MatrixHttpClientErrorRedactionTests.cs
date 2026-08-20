using System.Net;
using System.Net.Http.Headers;
using System.Text;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Extensions.Channels.Matrix.Tests;

/// <summary>
/// Guards the reflected-credential property behind #3398: provider-controlled response text from a
/// homeserver (or a proxy in front of it) must pass through <see cref="ISecretRedactor"/> before it
/// is interpolated into a <see cref="MatrixApiException"/> message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this matters.</b> <see cref="MatrixHttpClient"/> sets <c>Authorization: Bearer &lt;token&gt;</c>
/// on its default request headers. A homeserver that echoes the offending request headers back in its
/// error body - or puts them in the status line's reason phrase - would otherwise place the account
/// access token in an exception message that the gateway logs and that
/// <c>MatrixChannelAdapter</c> surfaces to agent-facing text.
/// </para>
/// <para>
/// <b>Non-vacuity.</b> Each test asserts BOTH that the token is absent AND that the redaction marker
/// is present, so removing the redaction call reddens the assertion rather than trivially passing on
/// an empty message.
/// </para>
/// </remarks>
public sealed class MatrixHttpClientErrorRedactionTests
{
    private const string Token = "syt_reflected_access_token_value";
    private const string Marker = "[REDACTED]";

    /// <summary>
    /// Minimal stand-in for the real redactor: replaces the one known secret. Using a fake rather
    /// than the production regex keeps the test about the SEAM (is the redactor consulted at all?)
    /// instead of about the redactor's pattern coverage, which is tested elsewhere.
    /// </summary>
    private sealed class TokenRedactor : ISecretRedactor
    {
        public string Redact(string input) =>
            string.IsNullOrEmpty(input) ? input : input.Replace(Token, Marker, StringComparison.Ordinal);

        public string RedactForExternalDelivery(string input) => Redact(input);
    }

    private static MatrixAccessToken MakeToken()
    {
        Assert.True(MatrixAccessToken.TryCreate(Token, out var token));
        return token;
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(response);
    }

    private static MatrixHttpClient CreateClient(HttpResponseMessage response) =>
        new(
            new HttpClient(new StubHandler(response)) { BaseAddress = new Uri("https://matrix.example.com/") },
            "@farnsworth:example.com",
            MakeToken(),
            new TokenRedactor());

    [Fact]
    public async Task SyncAsync_WhenErrorBodyReflectsTheAccessToken_MessageIsRedacted()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(
                $"{{\"errcode\":\"M_UNKNOWN_TOKEN\",\"error\":\"Invalid header Authorization: Bearer {Token}\"}}",
                Encoding.UTF8,
                "application/json"),
        };

        var client = CreateClient(response);

        var ex = await Assert.ThrowsAsync<MatrixApiException>(
            () => client.SyncAsync(since: null, timeoutMs: 1000, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
        // The diagnosis must survive redaction - status and errcode still classify the fault.
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal("M_UNKNOWN_TOKEN", ex.ErrorCode);
    }

    [Fact]
    public async Task SyncAsync_WhenReasonPhraseReflectsTheAccessTokenAndBodyIsUnparseable_MessageIsRedacted()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            // An HTML error page from a reverse proxy: no parseable Matrix error body, so the
            // message falls back to the reason phrase - the second reflection surface.
            Content = new StringContent("<html><body>Bad Gateway</body></html>", Encoding.UTF8, "text/html"),
            ReasonPhrase = $"Rejected Authorization Bearer {Token}",
        };

        var client = CreateClient(response);

        var ex = await Assert.ThrowsAsync<MatrixApiException>(
            () => client.SyncAsync(since: null, timeoutMs: 1000, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
    }

    [Fact]
    public async Task SendMessageAsync_WhenErrorBodyReflectsTheAccessToken_MessageIsRedacted()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent(
                $"{{\"errcode\":\"M_FORBIDDEN\",\"error\":\"token {Token} rejected\"}}",
                Encoding.UTF8,
                "application/json"),
        };

        var client = CreateClient(response);

        var ex = await Assert.ThrowsAsync<MatrixApiException>(
            () => client.SendMessageAsync("!room:example.com", new MatrixMessageContent { Body = "hi" }, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message, StringComparison.Ordinal);
        Assert.Contains(Marker, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutARedactor_TheClientStillReportsTheStatusDiagnosis()
    {
        // A null redactor is a deliberate no-op, not a blanket drop: an un-wired caller must keep
        // its diagnostics. This pins that contract so the fix cannot regress into silent loss.
        using var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("{\"errcode\":\"M_UNKNOWN\",\"error\":\"boom\"}", Encoding.UTF8, "application/json"),
        };

        var client = new MatrixHttpClient(
            new HttpClient(new StubHandler(response)) { BaseAddress = new Uri("https://matrix.example.com/") },
            "@farnsworth:example.com",
            MakeToken(),
            secretRedactor: null);

        var ex = await Assert.ThrowsAsync<MatrixApiException>(
            () => client.SyncAsync(since: null, timeoutMs: 1000, CancellationToken.None));

        Assert.Contains("boom", ex.Message, StringComparison.Ordinal);
        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorizationHeaderIsStillSetFromTheToken()
    {
        // Guards against "fixing" the leak by never sending the credential at all.
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        var http = new HttpClient(new StubHandler(response)) { BaseAddress = new Uri("https://matrix.example.com/") };
        _ = new MatrixHttpClient(http, "@farnsworth:example.com", MakeToken(), new TokenRedactor());

        AuthenticationHeaderValue? auth = http.DefaultRequestHeaders.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal(Token, auth.Parameter);
    }
}
