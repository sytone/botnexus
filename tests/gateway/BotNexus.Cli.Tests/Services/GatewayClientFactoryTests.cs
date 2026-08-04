using System.Net;
using BotNexus.Cli.Services;

namespace BotNexus.Cli.Tests;

/// <summary>
/// Specification for the single CLI gateway-client factory introduced by issue #2747.
///
/// The behaviour under test is a DISCRIMINATION, not a blanket block. Three cases must
/// hold simultaneously:
///   (a) the local default target still attaches an available ambient credential,
///   (b) an overridden <c>--url</c> with no explicit credential is refused, and
///   (c) an explicitly-supplied credential IS attached to an overridden URL.
/// A suite that only asserted (b) would be a suppression wearing a fix's clothes.
///
/// Clause 4 of the issue is an assertion of ABSENCE on a captured outbound request -
/// the locally-resolved credential must never appear on a request aimed at an
/// operator-supplied host - so it is asserted against a capturing handler rather than
/// against an exception type.
/// </summary>
public sealed class GatewayClientFactoryTests
{
    private const string AmbientSecret = "ambient-local-secret-value";
    private const string ExplicitToken = "explicit-operator-supplied-token";
    private const string RemoteUrl = "https://gateway.remote.example.com";
    private const string LocalDefaultUrl = "http://localhost:5005";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // ── AC2: the local default still gets the ambient credential ──────────────────

    [Fact]
    public async Task LocalDefaultUrl_AttachesAmbientCredential_ToOutboundRequest()
    {
        var handler = new CapturingHandler();
        var resolution = GatewayClientFactory.Resolve(
            LocalDefaultUrl, Timeout, explicitToken: null,
            new StubCredentialSource(AmbientSecret), handler);

        Assert.False(resolution.IsRefused, resolution.RefusalMessage);
        await SendProbeAsync(resolution);

        Assert.Contains(handler.CapturedHeaders,
            h => h.Name == GatewayClientFactory.CredentialHeaderName && h.Value == AmbientSecret);
    }

    [Fact]
    public async Task LocalDefaultUrl_WithNoCredentialAvailable_StillBuildsUnauthenticatedClient()
    {
        // Loopback development against an unauthenticated gateway must keep working;
        // the refusal is scoped to overridden targets, not to "no credential".
        var handler = new CapturingHandler();
        var resolution = GatewayClientFactory.Resolve(
            LocalDefaultUrl, Timeout, explicitToken: null,
            new StubCredentialSource(null), handler);

        Assert.False(resolution.IsRefused, resolution.RefusalMessage);
        await SendProbeAsync(resolution);

        Assert.Single(handler.CapturedRequestUris);
        Assert.DoesNotContain(handler.CapturedHeaders,
            h => h.Name == GatewayClientFactory.CredentialHeaderName);
    }

    // ── AC3: overridden URL with no explicit credential is refused ────────────────

    [Fact]
    public void OverriddenUrl_WithoutExplicitCredential_IsRefusedWithActionableMessage()
    {
        var resolution = GatewayClientFactory.Resolve(
            RemoteUrl, Timeout, explicitToken: null,
            new StubCredentialSource(AmbientSecret), new CapturingHandler());

        Assert.True(resolution.IsRefused);
        Assert.Null(resolution.Client);
        Assert.NotNull(resolution.RefusalMessage);
        // Actionable: it must name the offending target and the remedy.
        Assert.Contains(RemoteUrl, resolution.RefusalMessage, StringComparison.Ordinal);
        Assert.Contains("--token", resolution.RefusalMessage, StringComparison.Ordinal);
        // And it must never echo the credential it declined to send.
        Assert.DoesNotContain(AmbientSecret, resolution.RefusalMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void OverriddenUrl_WithWhitespaceExplicitCredential_IsRefused_FailingClosed()
    {
        var resolution = GatewayClientFactory.Resolve(
            RemoteUrl, Timeout, explicitToken: "   ",
            new StubCredentialSource(AmbientSecret), new CapturingHandler());

        Assert.True(resolution.IsRefused);
    }

    [Fact]
    public void MalformedUrl_IsRefused_FailingClosed()
    {
        var resolution = GatewayClientFactory.Resolve(
            "not-a-url", Timeout, explicitToken: ExplicitToken,
            new StubCredentialSource(AmbientSecret), new CapturingHandler());

        Assert.True(resolution.IsRefused);
    }

    // ── AC4: the leak direction, asserted as an ABSENCE on a captured request ─────

    [Fact]
    public async Task OverriddenUrl_DoesNotCarryLocallyResolvedCredential_OnAnyOutboundRequest()
    {
        var handler = new CapturingHandler();
        var resolution = GatewayClientFactory.Resolve(
            RemoteUrl, Timeout, explicitToken: null,
            new StubCredentialSource(AmbientSecret), handler);

        // Deliberately do NOT assert on refusal here. Whatever the factory hands back,
        // the ambient local credential must not reach the operator-supplied host. If a
        // future change makes the factory return a usable client for a remote target,
        // this test still holds it to the no-leak contract.
        await SendProbeAsync(resolution);

        Assert.DoesNotContain(handler.CapturedHeaders,
            h => h.Value.Contains(AmbientSecret, StringComparison.Ordinal));
    }

    // ── AC5: an explicit credential IS attached to an overridden URL ──────────────

    [Fact]
    public async Task OverriddenUrl_WithExplicitCredential_AttachesThatCredential()
    {
        var handler = new CapturingHandler();
        var resolution = GatewayClientFactory.Resolve(
            RemoteUrl, Timeout, explicitToken: ExplicitToken,
            new StubCredentialSource(AmbientSecret), handler);

        Assert.False(resolution.IsRefused, resolution.RefusalMessage);
        await SendProbeAsync(resolution);

        Assert.Contains(handler.CapturedHeaders,
            h => h.Name == GatewayClientFactory.CredentialHeaderName && h.Value == ExplicitToken);
        // The ambient local credential must not ride along beside the explicit one.
        Assert.DoesNotContain(handler.CapturedHeaders,
            h => h.Value.Contains(AmbientSecret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task LocalDefaultUrl_PrefersExplicitCredentialOverAmbient()
    {
        var handler = new CapturingHandler();
        var resolution = GatewayClientFactory.Resolve(
            LocalDefaultUrl, Timeout, explicitToken: ExplicitToken,
            new StubCredentialSource(AmbientSecret), handler);

        Assert.False(resolution.IsRefused, resolution.RefusalMessage);
        await SendProbeAsync(resolution);

        Assert.Contains(handler.CapturedHeaders,
            h => h.Name == GatewayClientFactory.CredentialHeaderName && h.Value == ExplicitToken);
    }

    // ── target classification ────────────────────────────────────────────────────

    [Theory]
    [InlineData("http://localhost:5005", true)]
    [InlineData("http://localhost:1", true)]
    [InlineData("http://127.0.0.1:5005", true)]
    [InlineData("http://[::1]:5005", true)]
    [InlineData("https://gateway.remote.example.com", false)]
    [InlineData("http://10.0.0.4:5005", false)]
    [InlineData("not-a-url", false)]
    [InlineData("ftp://localhost:5005", false)]
    public void IsLocalDefaultTarget_ClassifiesTargets(string url, bool expected)
        => Assert.Equal(expected, GatewayClientFactory.IsLocalDefaultTarget(url));

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static async Task SendProbeAsync(GatewayClientResolution resolution)
    {
        if (resolution.Client is null)
            return;

        using var client = resolution.Client;
        try
        {
            using var response = await client.GetAsync("/api/cron", CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            // Transport outcome is irrelevant; the assertion is on what was (not) sent.
        }
    }

    private sealed class StubCredentialSource(string? credential) : IGatewayCredentialSource
    {
        public string? GetGatewayCredential() => credential;
    }

    private sealed record CapturedHeader(string Name, string Value);

    /// <summary>
    /// Snapshots every outbound header value so absence can be asserted after the
    /// request message has been disposed by <see cref="HttpClient"/>.
    /// </summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedHeader> CapturedHeaders { get; } = [];
        public List<string> CapturedRequestUris { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequestUris.Add(request.RequestUri?.ToString() ?? "");
            foreach (var header in request.Headers)
            {
                foreach (var value in header.Value)
                    CapturedHeaders.Add(new CapturedHeader(header.Key, value));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
