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

    public static TheoryData<string, TimeSpan> ValidResetDurations => new()
    {
        { "1s", TimeSpan.FromSeconds(1) },
        { "6m0s", TimeSpan.FromMinutes(6) },
        { "1h2m3s", new TimeSpan(1, 2, 3) },
        { "150ms", TimeSpan.FromMilliseconds(150) },
        { "1.5s", TimeSpan.FromSeconds(1.5) },
    };

    public static TheoryData<string> InvalidResetDurations => new()
    {
        "1s2bananas",
        "2bananas1s",
        "1s2",
        "1..5s",
        ".s",
        "999999999999999999999999h",
    };

    [Theory]
    [MemberData(nameof(ValidResetDurations))]
    public void ParseReset_CompleteSupportedDuration_ReturnsExactInstant(string raw, TimeSpan duration)
    {
        var now = new DateTimeOffset(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);

        ProviderRateLimitHandler.ParseReset(raw, now).ShouldBe(now + duration);
    }

    [Fact]
    public void ParseReset_Rfc3339Timestamp_ReturnsExactInstant()
    {
        var now = new DateTimeOffset(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);
        var expected = new DateTimeOffset(2026, 9, 25, 8, 2, 3, TimeSpan.Zero);

        ProviderRateLimitHandler.ParseReset("2026-09-25T08:02:03Z", now).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(InvalidResetDurations))]
    public void ParseReset_PartiallyMalformedOrOutOfRangeDuration_ReturnsNull(string raw)
    {
        var now = new DateTimeOffset(2026, 9, 25, 7, 0, 0, TimeSpan.Zero);

        ProviderRateLimitHandler.ParseReset(raw, now).ShouldBeNull();
    }

    [Fact]
    public async Task SendAsync_MalformedResetHeader_ReturnsResponseAndRecordsUnknownReset()
    {
        var store = new ProviderUsageStore();
        using var client = new HttpClient(new ProviderRateLimitHandler(store, NullLogger<ProviderRateLimitHandler>.Instance)
        {
            InnerHandler = new MalformedResetResponseHandler(),
        });

        using var response = await client.GetAsync(new Uri("https://api.anthropic.com/v1/messages"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var snapshot = store.Snapshots["anthropic"];
        snapshot.RequestsLimit.ShouldBe(200);
        snapshot.RequestsResetUtc.ShouldBeNull();
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

    private sealed class MalformedResetResponseHandler : HttpMessageHandler
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
            response.Headers.Add("anthropic-ratelimit-requests-reset", "1s2bananas");
            return Task.FromResult(response);
        }
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
