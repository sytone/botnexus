using Azure.Core;
using Azure.Identity;
using BotNexus.Agent.Providers.MicrosoftFoundry;

namespace BotNexus.Agent.Providers.MicrosoftFoundry.Tests;

public class MicrosoftFoundryAuthenticationTests
{
    [Fact]
    public void CredentialFixtures_ExposeSupportedIdentityCompositionsThroughOneContract()
    {
        MicrosoftFoundryAuthentication.DefaultAzureCredential().Credential.ShouldBeOfType<DefaultAzureCredential>();
        MicrosoftFoundryAuthentication.SystemAssignedManagedIdentity().Credential.ShouldBeOfType<ManagedIdentityCredential>();
        MicrosoftFoundryAuthentication.UserAssignedManagedIdentity("client-id").Credential.ShouldBeOfType<ManagedIdentityCredential>();
    }

    [Fact]
    public async Task ApiKey_DoesNotInvokeTokenCredential()
    {
        var credential = new RecordingCredential(Token("unused", DateTimeOffset.UtcNow.AddHours(1)));
        var resolver = new MicrosoftFoundryAuthenticationResolver(
            MicrosoftFoundryAuthentication.ApiKey("foundry-key"), credential);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.services.ai.azure.com/openai/v1/responses");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "untrusted-token");
        request.Headers.TryAddWithoutValidation("api-key", new[] { "first-untrusted-key", "second-untrusted-key" });

        await resolver.ApplyAsync(request, CancellationToken.None);

        request.Headers.GetValues("api-key").ShouldBe(new[] { "foundry-key" });
        request.Headers.Authorization.ShouldBeNull();
        credential.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Entra_UsesFixedAudienceAndRefreshesBeforeExpiry()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var credential = new RecordingCredential(
            Token("first", now.AddMinutes(4)),
            Token("second", now.AddHours(1)));
        var resolver = new MicrosoftFoundryAuthenticationResolver(
            MicrosoftFoundryAuthentication.Entra(credential),
            utcNow: () => now);

        using var first = Request();
        await resolver.ApplyAsync(first, CancellationToken.None);
        using var second = Request();
        await resolver.ApplyAsync(second, CancellationToken.None);

        credential.CallCount.ShouldBe(2);
        credential.Contexts.Count.ShouldBe(2);
        foreach (var context in credential.Contexts)
            context.Scopes.ShouldBe(new[] { "https://ai.azure.com/.default" });
        second.Headers.Authorization?.Scheme.ShouldBe("Bearer");
        second.Headers.Authorization?.Parameter.ShouldBe("second");
    }

    [Fact]
    public async Task Entra_ConcurrentRefresh_IsSingleFlightPerResolverInstance()
    {
        var credential = new BlockingCredential(Token("shared", DateTimeOffset.UtcNow.AddHours(1)));
        var resolver = new MicrosoftFoundryAuthenticationResolver(
            MicrosoftFoundryAuthentication.Entra(credential));
        var requests = Enumerable.Range(0, 12).Select(_ => Request()).ToArray();

        var applies = requests.Select(request => resolver.ApplyAsync(request, CancellationToken.None).AsTask()).ToArray();
        await credential.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        credential.Release.TrySetResult();
        await Task.WhenAll(applies).WaitAsync(TimeSpan.FromSeconds(5));

        credential.CallCount.ShouldBe(1);
        foreach (var request in requests)
            request.Headers.Authorization?.Parameter.ShouldBe("shared");
        foreach (var request in requests)
            request.Dispose();
    }

    private static HttpRequestMessage Request() =>
        new(HttpMethod.Post, "https://example.services.ai.azure.com/openai/v1/responses");

    private static AccessToken Token(string value, DateTimeOffset expiresOn) => new(value, expiresOn);

    private sealed class RecordingCredential(params AccessToken[] tokens) : TokenCredential
    {
        private int _callCount;
        public int CallCount => _callCount;
        public List<TokenRequestContext> Contexts { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            Contexts.Add(requestContext);
            var index = Interlocked.Increment(ref _callCount) - 1;
            return ValueTask.FromResult(tokens[Math.Min(index, tokens.Length - 1)]);
        }
    }

    private sealed class BlockingCredential(AccessToken token) : TokenCredential
    {
        private int _callCount;
        public int CallCount => _callCount;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return token;
        }
    }
}
