using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Tests.Models;

public class ImmutableOptionsTests
{
    [Fact]
    public void Usage_WithExpression_CreatesNewInstance_WithoutMutatingOriginal()
    {
        var original = new Usage
        {
            Input = 10,
            Output = 3,
            CacheRead = 1,
            CacheWrite = 2,
            TotalTokens = 16,
            Cost = new UsageCost(0.1m, 0.2m, 0.01m, 0.02m, 0.33m)
        };

        var clone = original with { Output = 9 };

        clone.ShouldNotBeSameAs(original);
        clone.Output.ShouldBe(9);
        original.Output.ShouldBe(3);
        original.TotalTokens.ShouldBe(16);
    }

    [Fact]
    public void Usage_WithExpression_DoesNotLeakStateThroughNestedRecords()
    {
        var original = new Usage
        {
            Input = 5,
            Output = 6,
            Cost = new UsageCost(0.01m, 0.02m, 0.003m, 0.004m, 0.037m)
        };

        var clone = original with
        {
            Cost = original.Cost with { Total = 0.999m }
        };

        clone.Cost.Total.ShouldBe(0.999m);
        original.Cost.Total.ShouldBe(0.037m);
    }

    [Fact]
    public void StreamOptions_WithExpression_ClonesAndCarriesAllProperties()
    {
        var cts = new CancellationTokenSource();
        var originalHeaders = new Dictionary<string, string> { ["x-one"] = "1" };
        var originalMetadata = new Dictionary<string, object> { ["traceId"] = "abc" };
        var original = new BotNexus.Agent.Providers.Core.StreamOptions
        {
            Temperature = 0.7f,
            MaxTokens = 1234,
            CancellationToken = cts.Token,
            ApiKey = "key",
            Transport = Transport.WebSocket,
            CacheRetention = CacheRetention.Long,
            SessionId = "session-1",
            Headers = originalHeaders,
            MaxRetryDelayMs = 777,
            Metadata = originalMetadata
        };

        var clone = original with { Temperature = 0.9f };

        clone.ShouldNotBeSameAs(original);
        clone.Temperature.ShouldBe(0.9f);
        clone.MaxTokens.ShouldBe(original.MaxTokens);
        clone.CancellationToken.ShouldBe(original.CancellationToken);
        clone.ApiKey.ShouldBe(original.ApiKey);
        clone.Transport.ShouldBe(original.Transport);
        clone.CacheRetention.ShouldBe(original.CacheRetention);
        clone.SessionId.ShouldBe(original.SessionId);
        clone.MaxRetryDelayMs.ShouldBe(original.MaxRetryDelayMs);
        clone.Headers.ShouldNotBeSameAs(originalHeaders);
        clone.Headers.ShouldBe(originalHeaders);
        clone.Metadata.ShouldNotBeSameAs(originalMetadata);
        clone.Metadata.ShouldBe(originalMetadata);
        original.Temperature.ShouldBe(0.7f);
    }

    [Fact]
    public void StreamOptions_Copies_ShouldNotLeakMutableDictionaryState()
    {
        var original = new BotNexus.Agent.Providers.Core.StreamOptions
        {
            Headers = new Dictionary<string, string> { ["x-key"] = "v1" },
            Metadata = new Dictionary<string, object> { ["flag"] = "on" }
        };

        var clone = original with { };
        clone.Headers!["x-key"] = "changed";
        clone.Metadata!["flag"] = "changed";

        original.Headers!["x-key"].ShouldBe("v1");
        original.Metadata!["flag"].ShouldBe("on");
    }
}
