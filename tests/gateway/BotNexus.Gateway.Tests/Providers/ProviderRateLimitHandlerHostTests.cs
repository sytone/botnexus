using System.Net;
using BotNexus.Gateway.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class ProviderRateLimitHandlerHostTests
{
    public static TheoryData<string, string> KnownProviderHosts => new()
    {
        { "anthropic.com", "anthropic" },
        { "API.ANTHROPIC.COM", "anthropic" },
        { "openai.com", "openai" },
        { "api.openai.com", "openai" },
        { "githubcopilot.com", "github-copilot" },
        { "api.githubcopilot.com", "github-copilot" },
        { "models.github.ai", "github-models" },
        { "api.models.github.ai", "github-models" },
        { "inference.ai.azure.com", "github-models" },
        { "region.inference.ai.azure.com", "github-models" },
    };

    public static TheoryData<string> UnknownProviderHosts => new()
    {
        "notanthropic.com",
        "notopenai.com",
        "notgithubcopilot.com",
        "notmodels.github.ai",
        "notinference.ai.azure.com",
        "anthropic.com.example",
        "localhost",
    };

    [Theory]
    [MemberData(nameof(KnownProviderHosts))]
    public void ResolveProvider_ExactDomainOrDotDelimitedSubdomain_ReturnsCanonicalProvider(
        string host,
        string expectedProvider)
    {
        ProviderRateLimitHandler.ResolveProvider(new Uri($"https://{host}/v1/messages"))
            .ShouldBe(expectedProvider);
    }

    [Theory]
    [MemberData(nameof(UnknownProviderHosts))]
    public void ResolveProvider_ConcatenatedLookalikeOrUnknownHost_ReturnsNull(string host)
    {
        ProviderRateLimitHandler.ResolveProvider(new Uri($"https://{host}/v1/messages"))
            .ShouldBeNull();
    }

    [Fact]
    public void ResolveProvider_NullUri_ReturnsNull()
    {
        ProviderRateLimitHandler.ResolveProvider(null).ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_ConcatenatedLookalikeHost_DoesNotReplaceKnownProviderSnapshot()
    {
        var store = new ProviderUsageStore();
        var original = new ProviderRateLimitSnapshot("anthropic", RequestsLimit: 100, RequestsRemaining: 90);
        store.Record(original, model: null);

        using var client = new HttpClient(new ProviderRateLimitHandler(store, NullLogger<ProviderRateLimitHandler>.Instance)
        {
            InnerHandler = new RateLimitResponseHandler(),
        });

        using var response = await client.GetAsync(new Uri("https://notanthropic.com/v1/messages"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        store.Snapshots["anthropic"].ShouldBeSameAs(original);
    }

    private sealed class RateLimitResponseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
            };
            response.Headers.Add("anthropic-ratelimit-requests-limit", "200");
            response.Headers.Add("anthropic-ratelimit-requests-remaining", "150");
            return Task.FromResult(response);
        }
    }
}
