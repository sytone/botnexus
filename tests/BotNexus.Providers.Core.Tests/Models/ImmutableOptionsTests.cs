using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Models;

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

        clone.Should().NotBeSameAs(original);
        clone.Output.Should().Be(9);
        original.Output.Should().Be(3);
        original.TotalTokens.Should().Be(16);
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

        clone.Cost.Total.Should().Be(0.999m);
        original.Cost.Total.Should().Be(0.037m);
    }

    [Fact]
    public void StreamOptions_WithExpression_ClonesAndCarriesAllProperties()
    {
        var cts = new CancellationTokenSource();
        var originalHeaders = new Dictionary<string, string> { ["x-one"] = "1" };
        var originalMetadata = new Dictionary<string, object> { ["traceId"] = "abc" };
        var original = new Core.StreamOptions
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

        clone.Should().NotBeSameAs(original);
        clone.Temperature.Should().Be(0.9f);
        clone.MaxTokens.Should().Be(original.MaxTokens);
        clone.CancellationToken.Should().Be(original.CancellationToken);
        clone.ApiKey.Should().Be(original.ApiKey);
        clone.Transport.Should().Be(original.Transport);
        clone.CacheRetention.Should().Be(original.CacheRetention);
        clone.SessionId.Should().Be(original.SessionId);
        clone.MaxRetryDelayMs.Should().Be(original.MaxRetryDelayMs);
        clone.Headers.Should().NotBeSameAs(originalHeaders);
        clone.Headers.Should().BeEquivalentTo(originalHeaders);
        clone.Metadata.Should().NotBeSameAs(originalMetadata);
        clone.Metadata.Should().BeEquivalentTo(originalMetadata);
        original.Temperature.Should().Be(0.7f);
    }

    [Fact]
    public void StreamOptions_Copies_ShouldNotLeakMutableDictionaryState()
    {
        var original = new Core.StreamOptions
        {
            Headers = new Dictionary<string, string> { ["x-key"] = "v1" },
            Metadata = new Dictionary<string, object> { ["flag"] = "on" }
        };

        var clone = original with { };
        clone.Headers!["x-key"] = "changed";
        clone.Metadata!["flag"] = "changed";

        original.Headers!["x-key"].Should().Be("v1");
        original.Metadata!["flag"].Should().Be("on");
    }
}
