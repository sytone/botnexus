using System.Net;
using Azure.Core;
using Azure.Identity;
using BotNexus.Agent.Providers.Core.Registry;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.MicrosoftFoundry.Tests;

public sealed class MicrosoftFoundryHealthTests
{
    [Fact]
    public async Task CheckAsync_EntraSuccess_UsesInstanceAudienceAndCacheWithoutApiKeyFallback()
    {
        var credential = new FakeCredential();
        var handler = new ProbeHandler(HttpStatusCode.OK);
        using var provider = Create(credential, handler);
        var health = ((object)provider).ShouldBeAssignableTo<IProviderHealthCheck>();

        var first = await health.CheckAsync("alpha");
        var second = await health.CheckAsync("alpha");

        first.Status.ShouldBe(ProviderHealthStatus.Healthy);
        first.HasCredentials.ShouldBeTrue();
        first.Error.ShouldBeNull();
        second.Status.ShouldBe(ProviderHealthStatus.Healthy);
        credential.Calls.ShouldBe(1);
        credential.Scopes.ShouldBe(new[] { MicrosoftFoundryAuthenticationResolver.EntraAudience });
        handler.Sends.ShouldBe(2);
        handler.Uri.ShouldBe(new Uri("https://alpha.services.ai.azure.com/openai/v1/models"));
        handler.Method.ShouldBe(HttpMethod.Get);
        handler.Authorization.ShouldBe("Bearer synthetic-secret");
        handler.HasApiKey.ShouldBeFalse();
    }

    [Theory]
    [InlineData(401, "HTTP 401", false)]
    [InlineData(403, "RBAC", true)]
    [InlineData(429, "HTTP 429", true)]
    [InlineData(503, "HTTP 503", true)]
    [InlineData(300, "redirect", true)]
    [InlineData(301, "redirect", true)]
    [InlineData(302, "redirect", true)]
    [InlineData(303, "redirect", true)]
    [InlineData(304, "redirect", true)]
    [InlineData(305, "redirect", true)]
    [InlineData(306, "redirect", true)]
    [InlineData(307, "redirect", true)]
    [InlineData(308, "redirect", true)]
    public async Task CheckAsync_HttpFailure_ReturnsSanitizedDiagnostic(int status, string diagnostic, bool hasCredentials)
    {
        var handler = new ProbeHandler((HttpStatusCode)status);
        using var provider = Create(new FakeCredential(), handler);
        var health = ((object)provider).ShouldBeAssignableTo<IProviderHealthCheck>();

        var result = await health.CheckAsync("alpha");

        result.Status.ShouldBe(ProviderHealthStatus.Unhealthy);
        result.HasCredentials.ShouldBe(hasCredentials);
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain(diagnostic);
        result.Error.ShouldNotContain("synthetic-secret");
        result.Error.ShouldNotContain("reason-canary");
        result.Error.ShouldNotContain("location-canary");
        handler.Content.Reads.ShouldBe(0);
        handler.Sends.ShouldBe(1);
    }

    [Theory]
    [InlineData(true, "Credential unavailable")]
    [InlineData(false, "Token acquisition failed")]
    public async Task CheckAsync_CredentialFailure_DoesNotSendOrLeakOrFallback(bool unavailable, string diagnostic)
    {
        var credential = new FakeCredential
        {
            Failure = unavailable
                ? new CredentialUnavailableException("synthetic-secret")
                : new AuthenticationFailedException("synthetic-secret")
        };
        var handler = new ProbeHandler(HttpStatusCode.OK);
        using var provider = Create(credential, handler);
        var health = ((object)provider).ShouldBeAssignableTo<IProviderHealthCheck>();

        var result = await health.CheckAsync("alpha");

        result.Status.ShouldBe(ProviderHealthStatus.Unhealthy);
        result.HasCredentials.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain(diagnostic);
        result.Error.ShouldNotContain("synthetic-secret");
        handler.Sends.ShouldBe(0);
    }

    [Fact]
    public async Task CheckAsync_CallerCancellation_PropagatesWithoutSending()
    {
        var handler = new ProbeHandler(HttpStatusCode.OK);
        var credential = new FakeCredential();
        using var provider = Create(credential, handler);
        var health = ((object)provider).ShouldBeAssignableTo<IProviderHealthCheck>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => health.CheckAsync("alpha", cancellation.Token));

        handler.Sends.ShouldBe(0);
        credential.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task CheckAsync_CancellationDuringTokenAcquisition_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var credential = new FakeCredential { Cancel = cancellation };
        var handler = new ProbeHandler(HttpStatusCode.OK);
        using var provider = Create(credential, handler);
        var health = ((object)provider).ShouldBeAssignableTo<IProviderHealthCheck>();

        await Should.ThrowAsync<OperationCanceledException>(() => health.CheckAsync("alpha", cancellation.Token));

        handler.Sends.ShouldBe(0);
    }

    [Fact]
    public async Task CheckAsync_HttpTransportFailure_SanitizesException()
    {
        var handler = new ProbeHandler(HttpStatusCode.OK) { FailTransport = true };
        using var provider = Create(new FakeCredential(), handler);
        var health = ((object)provider).ShouldBeAssignableTo<IProviderHealthCheck>();

        var result = await health.CheckAsync("alpha");

        result.Status.ShouldBe(ProviderHealthStatus.Unhealthy);
        result.HasCredentials.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("Connectivity");
        result.Error.ShouldNotContain("synthetic-secret");
    }

    [Fact]
    public async Task CheckAsync_NamedInstances_KeepEndpointCredentialsAndCacheIsolated()
    {
        var alpha = new FakeCredential();
        var beta = new FakeCredential { Token = "beta-secret" };
        var alphaHandler = new ProbeHandler(HttpStatusCode.OK);
        var betaHandler = new ProbeHandler(HttpStatusCode.OK);
        using var provider = new MicrosoftFoundryResponsesProvider(
            [new("alpha", new Uri("https://alpha.services.ai.azure.com"), MicrosoftFoundryAuthentication.Entra(alpha)),
             new("beta", new Uri("https://beta.services.ai.azure.com"), MicrosoftFoundryAuthentication.Entra(beta))],
            name => name == "alpha" ? alphaHandler : betaHandler,
            NullLogger<MicrosoftFoundryResponsesProvider>.Instance);
        var health = ((object)provider).ShouldBeAssignableTo<IProviderHealthCheck>();

        (await health.CheckAsync("alpha")).Status.ShouldBe(ProviderHealthStatus.Healthy);
        (await health.CheckAsync("beta")).Status.ShouldBe(ProviderHealthStatus.Healthy);
        (await health.CheckAsync("missing")).Status.ShouldBe(ProviderHealthStatus.Unhealthy);

        alpha.Calls.ShouldBe(1);
        beta.Calls.ShouldBe(1);
        alphaHandler.Authorization.ShouldBe("Bearer synthetic-secret");
        betaHandler.Authorization.ShouldBe("Bearer beta-secret");
        betaHandler.Uri.ShouldBe(new Uri("https://beta.services.ai.azure.com/openai/v1/models"));
        alphaHandler.Sends.ShouldBe(1);
        betaHandler.Sends.ShouldBe(1);
    }

    [Fact]
    public async Task CheckAsync_ApiKeyMode_UsesOnlyConfiguredKey()
    {
        var handler = new ProbeHandler(HttpStatusCode.OK);
        using var provider = new MicrosoftFoundryResponsesProvider(
            [new("alpha", new Uri("https://alpha.services.ai.azure.com"), MicrosoftFoundryAuthentication.ApiKey("synthetic-key"))],
            _ => handler, NullLogger<MicrosoftFoundryResponsesProvider>.Instance);
        var health = ((object)provider).ShouldBeAssignableTo<IProviderHealthCheck>();

        (await health.CheckAsync("alpha")).Status.ShouldBe(ProviderHealthStatus.Healthy);

        handler.Authorization.ShouldBeNull();
        handler.ApiKey.ShouldBe("synthetic-key");
    }

    [Fact]
    public async Task CheckAsync_Success_DoesNotBufferOrConsumeResponseBody()
    {
        var handler = new ProbeHandler(HttpStatusCode.OK);
        using var provider = Create(new FakeCredential(), handler);

        var result = await provider.CheckAsync("alpha");

        result.Status.ShouldBe(ProviderHealthStatus.Healthy);
        handler.Content.Reads.ShouldBe(0);
        handler.Content.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task CheckAsync_InFlightHttpCancellation_PreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new WaitingHandler();
        using var provider = new MicrosoftFoundryResponsesProvider(
            [new("alpha", new Uri("https://alpha.services.ai.azure.com"), MicrosoftFoundryAuthentication.Entra(new FakeCredential()))],
            _ => handler, NullLogger<MicrosoftFoundryResponsesProvider>.Instance);
        var pending = provider.CheckAsync("alpha", cancellation.Token);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        handler.Sends.ShouldBe(1);
    }

    [Fact]
    public async Task CheckAsync_DirectCall_CooperativelyTimesOutWithoutController()
    {
        var handler = new WaitingHandler();
        using var provider = new MicrosoftFoundryResponsesProvider(
            [new("alpha", new Uri("https://alpha.services.ai.azure.com"), MicrosoftFoundryAuthentication.Entra(new FakeCredential()))],
            _ => handler, NullLogger<MicrosoftFoundryResponsesProvider>.Instance);

        var result = await provider.CheckAsync("alpha").WaitAsync(TimeSpan.FromSeconds(20));

        result.Status.ShouldBe(ProviderHealthStatus.Unhealthy);
        result.Error.ShouldBe("Microsoft Foundry health check timed out.");
        result.HasCredentials.ShouldBeTrue();
        handler.Sends.ShouldBe(1);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Sends { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            Entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation sentinel must not complete normally.");
        }
    }

    private sealed class CanaryContent : HttpContent
    {
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Reads++;
            throw new InvalidOperationException("synthetic-secret body must never be consumed");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static MicrosoftFoundryResponsesProvider Create(FakeCredential credential, ProbeHandler handler) => new(
        [new("alpha", new Uri("https://alpha.services.ai.azure.com"), MicrosoftFoundryAuthentication.Entra(credential))],
        _ => handler, NullLogger<MicrosoftFoundryResponsesProvider>.Instance);

    private sealed class FakeCredential : TokenCredential
    {
        public int Calls { get; private set; }
        public string[] Scopes { get; private set; } = [];
        public string Token { get; init; } = "synthetic-secret";
        public Exception? Failure { get; init; }
        public CancellationTokenSource? Cancel { get; init; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Calls++;
            Scopes = requestContext.Scopes;
            Cancel?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is { } failure)
                throw failure;
            return ValueTask.FromResult(new AccessToken(Token, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class ProbeHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Authorization { get; private set; }
        public bool HasApiKey { get; private set; }
        public string? ApiKey { get; private set; }
        public bool FailTransport { get; init; }
        public CanaryContent Content { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sends++;
            Uri = request.RequestUri;
            Method = request.Method;
            Authorization = request.Headers.Authorization?.ToString();
            HasApiKey = request.Headers.TryGetValues("api-key", out var keys);
            ApiKey = keys?.Single();
            if (FailTransport)
                throw new HttpRequestException("synthetic-secret");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = Content,
                ReasonPhrase = "reason-canary synthetic-secret",
                Headers = { Location = new Uri("https://unapproved.example/location-canary") }
            });
        }
    }
}
