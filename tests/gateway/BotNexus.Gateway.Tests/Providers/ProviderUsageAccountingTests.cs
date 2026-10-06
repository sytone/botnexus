using System.Net;
using System.Text;
using BotNexus.Agent.Providers.Core.Resilience;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class ProviderUsageAccountingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-18T21:00:00Z");
    private static readonly DateTimeOffset Reset = Start.AddHours(1);

    [Fact]
    public async Task Handler_records_supported_calls_without_headers_or_model()
    {
        var store = Store();
        using var handler = new ProviderRateLimitHandler(store, NullLogger<ProviderRateLimitHandler>.Instance)
        {
            InnerHandler = new SequenceHandler(new HttpResponseMessage(HttpStatusCode.OK)),
        };
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent("{\"messages\":[]}", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);

        var sample = Assert.Single(store.QuerySince(DateTimeOffset.MinValue).Samples);
        Assert.Null(sample.Model);
        Assert.Equal(1, sample.Requests);
        Assert.Equal(0, sample.Failures);
    }

    [Fact]
    public async Task Handler_records_large_request_and_failed_unparseable_request()
    {
        var store = Store();
        using var handler = new ProviderRateLimitHandler(store, NullLogger<ProviderRateLimitHandler>.Instance)
        {
            InnerHandler = new SequenceHandler(
                new HttpResponseMessage(HttpStatusCode.OK),
                new HttpResponseMessage(HttpStatusCode.BadRequest)),
        };
        using var client = new HttpClient(handler);

        using var large = new StringContent("{\"model\":\"ignored\",\"payload\":\"" + new string('x', 70_000) + "\"}");
        using var invalid = new StringContent("not-json");
        using var first = await client.PostAsync("https://api.openai.com/v1/responses", large);
        using var second = await client.PostAsync("https://api.openai.com/v1/responses", invalid);

        var samples = store.QuerySince(DateTimeOffset.MinValue).Samples;
        Assert.Equal(2, samples.Count);
        Assert.All(samples, sample => Assert.Null(sample.Model));
        Assert.Equal(1, samples.Sum(sample => sample.Failures));
    }

    [Fact]
    public void Partial_snapshots_advance_each_token_baseline_independently()
    {
        var store = Store();
        store.Record(Snapshot(inputUsed: 100, outputUsed: 50, totalUsed: 150, Start), "model-a");
        store.Record(Snapshot(inputUsed: 125, outputUsed: null, totalUsed: null, Start.AddSeconds(1)), "model-a");
        store.Record(Snapshot(inputUsed: null, outputUsed: 80, totalUsed: 205, Start.AddSeconds(2)), "model-a");
        store.Record(new ProviderRateLimitSnapshot("anthropic", RequestsLimit: 100, RequestsRemaining: 90,
            ObservedAtUtc: Start.AddSeconds(3)), "model-a");
        store.Record(Snapshot(inputUsed: 140, outputUsed: 90, totalUsed: 230, Start.AddSeconds(4)), "model-a");

        var samples = store.QuerySince(Start).Samples;
        Assert.Equal(140, samples.Sum(sample => sample.InputTokens ?? 0));
        Assert.Equal(90, samples.Sum(sample => sample.OutputTokens ?? 0));
        Assert.Equal(230, samples.Sum(sample => sample.TotalTokens ?? 0));
        Assert.Null(samples[3].InputTokens);
        Assert.Null(samples[3].OutputTokens);
        Assert.Null(samples[3].TotalTokens);
    }

    [Fact]
    public void Combined_total_is_preserved_without_fabricating_split_tokens()
    {
        var store = Store();
        store.Record(Snapshot(inputUsed: null, outputUsed: null, totalUsed: 40, Start), "model-a");

        var sample = Assert.Single(store.QuerySince(Start).Samples);
        Assert.Null(sample.InputTokens);
        Assert.Null(sample.OutputTokens);
        Assert.Equal(40, sample.TotalTokens);
    }

    [Fact]
    public void Out_of_order_observation_is_counted_but_does_not_rewind_any_baseline()
    {
        var store = Store();
        store.Record(Snapshot(100, 50, 150, Start), "model-a");
        store.Record(Snapshot(150, 75, 225, Start.AddSeconds(2)), "model-a");
        store.Record(Snapshot(125, 60, 185, Start.AddSeconds(1)), "model-a");
        store.Record(Snapshot(175, 90, 265, Start.AddSeconds(3)), "model-a");

        var samples = store.QuerySince(Start).Samples;
        Assert.Equal(4, samples.Sum(sample => sample.Requests));
        Assert.Equal(175, samples.Sum(sample => sample.InputTokens ?? 0));
        Assert.Equal(90, samples.Sum(sample => sample.OutputTokens ?? 0));
        Assert.Equal(265, samples.Sum(sample => sample.TotalTokens ?? 0));
        Assert.Null(samples[2].InputTokens);
        Assert.Null(samples[2].OutputTokens);
        Assert.Null(samples[2].TotalTokens);
    }

    [Fact]
    public void Atomic_provider_transition_consumes_each_ordered_counter_increment_once()
    {
        var store = Store();
        store.Record(Snapshot(0, 0, 0, Start), "model-a");
        for (var used = 1; used <= 32; used++)
            store.Record(Snapshot(used, used * 2, used * 3, Start.AddTicks(used)), "model-a");

        var samples = store.QuerySince(Start).Samples;
        Assert.Equal(33, samples.Sum(sample => sample.Requests));
        Assert.Equal(32, samples.Sum(sample => sample.InputTokens ?? 0));
        Assert.Equal(64, samples.Sum(sample => sample.OutputTokens ?? 0));
        Assert.Equal(96, samples.Sum(sample => sample.TotalTokens ?? 0));
    }

    [Fact]
    public void Query_at_20001_global_boundary_reports_exact_window_truncation()
    {
        var store = Store();
        store.Record(Snapshot(0, 0, 0, Start), "provider-a-model");
        for (var i = 1; i <= 20_000; i++)
        {
            store.Record(new ProviderRateLimitSnapshot(
                "provider-b",
                ObservedAtUtc: Start.AddTicks(i)),
                "provider-b-model");
        }

        var query = store.QuerySince(Start);
        Assert.True(query.IsTruncated);
        Assert.Equal(20_000, query.Samples.Count);
        Assert.DoesNotContain(query.Samples, sample => sample.Provider == "anthropic");
        Assert.All(query.Samples, sample => Assert.Equal("provider-b", sample.Provider));
    }

    [Fact]
    public void Api_distinguishes_unavailable_model_from_literal_unknown_and_exposes_combined_only_tokens()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new ProviderUsageStore(new FixedTimeProvider(now));
        store.Record(Snapshot(null, null, 10, now), null);
        store.Record(Snapshot(null, null, 15, now.AddSeconds(1)), "unknown");
        var controller = new ProviderUsageController(store);

        var ok = Assert.IsType<OkObjectResult>(controller.GetUsage(1440));
        var payload = Assert.IsType<ProviderUsageResponseDto>(ok.Value);
        var provider = Assert.Single(payload.Providers);
        Assert.Null(provider.Burn.InputTokens);
        Assert.Null(provider.Burn.OutputTokens);
        Assert.Equal(15, provider.Burn.TotalTokens);
        Assert.Contains(provider.Burn.Models, model =>
            !model.ModelKnown && model.Model is null && model.ModelDisplayName == "Unknown model");
        Assert.Contains(provider.Burn.Models, model =>
            model.ModelKnown && model.Model == "unknown" && model.ModelDisplayName == "unknown");
    }

    [Fact]
    public async Task Retry_composition_counts_each_response_wire_attempt()
    {
        var store = Store();
        using var usage = new ProviderRateLimitHandler(store, NullLogger<ProviderRateLimitHandler>.Instance)
        {
            InnerHandler = new SequenceHandler(
                new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                new HttpResponseMessage(HttpStatusCode.OK)),
        };
        using var retry = new TransientHttpRetryHandler(maxRetries: 1, baseDelay: TimeSpan.FromMilliseconds(1), randomSource: () => 0)
        {
            InnerHandler = usage,
        };
        using var client = new HttpClient(retry);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent("{\"model\":\"model-a\"}", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var samples = store.QuerySince(DateTimeOffset.MinValue).Samples;
        Assert.Equal(2, samples.Sum(sample => sample.Requests));
        Assert.Equal(1, samples.Sum(sample => sample.Failures));
    }

    [Fact]
    public async Task Transport_exception_without_response_is_not_counted()
    {
        var store = Store();
        using var handler = new ProviderRateLimitHandler(store, NullLogger<ProviderRateLimitHandler>.Instance)
        {
            InnerHandler = new ThrowingHandler(),
        };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://api.anthropic.com/v1/messages"));
        Assert.Empty(store.QuerySince(DateTimeOffset.MinValue).Samples);
    }

    private static ProviderUsageStore Store() => new(new FixedTimeProvider(Start.AddMinutes(30)));

    private static ProviderRateLimitSnapshot Snapshot(
        long? inputUsed,
        long? outputUsed,
        long? totalUsed,
        DateTimeOffset observedAt) => new(
            Provider: "anthropic",
            InputTokensLimit: inputUsed is null ? null : 10_000,
            InputTokensRemaining: inputUsed is null ? null : 10_000 - inputUsed,
            InputTokensResetUtc: inputUsed is null ? null : Reset,
            OutputTokensLimit: outputUsed is null ? null : 10_000,
            OutputTokensRemaining: outputUsed is null ? null : 10_000 - outputUsed,
            OutputTokensResetUtc: outputUsed is null ? null : Reset,
            TokensLimit: totalUsed is null ? null : 20_000,
            TokensRemaining: totalUsed is null ? null : 20_000 - totalUsed,
            TokensResetUtc: totalUsed is null ? null : Reset,
            ObservedAtUtc: observedAt);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responses[Interlocked.Increment(ref _index) - 1];
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("transport failed");
    }
}
