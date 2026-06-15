using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Unit coverage for the shared Responses-stream primitives promoted from the per-provider parsers
/// to Providers.Core (step 5/6 of #1377). The provider-level <c>CopilotResponsesProviderParityTests</c>
/// remain the behavioral safety net; these tests pin the promoted helpers' contract directly.
/// </summary>
public class ResponsesStreamPrimitivesTests
{
    private static LlmModel Model() => new(
        Id: "gpt-5",
        Name: "GPT-5",
        Api: "openai-responses",
        Provider: "openai",
        BaseUrl: "https://api.openai.com",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(1.0m, 2.0m, 0.5m, 1.5m),
        ContextWindow: 200000,
        MaxTokens: 16384);

    [Fact]
    public void ComposeToolCallId_WithItemId_JoinsWithPipe()
    {
        ResponsesStreamHelpers.ComposeToolCallId("call_abc", "item_123").ShouldBe("call_abc|item_123");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ComposeToolCallId_WithoutItemId_ReturnsCallIdOnly(string? itemId)
    {
        ResponsesStreamHelpers.ComposeToolCallId("call_abc", itemId).ShouldBe("call_abc");
    }

    [Theory]
    [InlineData("completed", StopReason.Stop)]
    [InlineData("incomplete", StopReason.Length)]
    [InlineData("refusal", StopReason.Refusal)]
    [InlineData("content_filter", StopReason.Sensitive)]
    [InlineData("failed", StopReason.Error)]
    [InlineData("cancelled", StopReason.Error)]
    [InlineData("in_progress", StopReason.Stop)]
    [InlineData("queued", StopReason.Stop)]
    [InlineData("something_unknown", StopReason.Stop)]
    [InlineData(null, StopReason.Stop)]
    public void MapStopReason_MapsKnownStatusesAndFallsBackToStop(string? status, StopReason expected)
    {
        ResponsesStreamHelpers.MapStopReason(status).ShouldBe(expected);
    }

    [Fact]
    public void ParseUsage_FoldsCacheTokensOutOfInputAndAttachesCost()
    {
        using var doc = JsonDocument.Parse(
            """
            {
                "input_tokens": 100,
                "output_tokens": 40,
                "total_tokens": 140,
                "input_tokens_details": { "cached_tokens": 30, "cache_write_tokens": 10 }
            }
            """);

        var usage = ResponsesStreamHelpers.ParseUsage(doc.RootElement, Model());

        // input billed = 100 - cacheRead(30) - cacheWrite(10) = 60
        usage.Input.ShouldBe(60);
        usage.Output.ShouldBe(40);
        usage.CacheRead.ShouldBe(30);
        usage.CacheWrite.ShouldBe(10);
        usage.TotalTokens.ShouldBe(140);
        usage.Cost.ShouldNotBeNull();
        usage.Cost.Total.ShouldBeGreaterThan(0m);
    }

    [Fact]
    public void ParseUsage_MissingFields_DefaultToZeroAndComputeTotal()
    {
        using var doc = JsonDocument.Parse("""{ "input_tokens": 10, "output_tokens": 5 }""");

        var usage = ResponsesStreamHelpers.ParseUsage(doc.RootElement, Model());

        usage.Input.ShouldBe(10);
        usage.Output.ShouldBe(5);
        usage.CacheRead.ShouldBe(0);
        usage.CacheWrite.ShouldBe(0);
        // total absent -> input + output
        usage.TotalTokens.ShouldBe(15);
    }

    [Fact]
    public void ParseUsage_InputNeverGoesNegative_WhenCacheExceedsInput()
    {
        using var doc = JsonDocument.Parse(
            """
            {
                "input_tokens": 5,
                "output_tokens": 2,
                "input_tokens_details": { "cached_tokens": 20 }
            }
            """);

        var usage = ResponsesStreamHelpers.ParseUsage(doc.RootElement, Model());

        usage.Input.ShouldBe(0);
        usage.CacheRead.ShouldBe(20);
    }

    [Fact]
    public void SseEvent_IsValueEqualityRecord()
    {
        var a = new SseEvent("response.completed", "{}");
        var b = new SseEvent("response.completed", "{}");
        a.ShouldBe(b);
        a.Event.ShouldBe("response.completed");
        a.Data.ShouldBe("{}");
    }

    [Fact]
    public void ToolState_AccumulatesArgumentsAndExposesIdentity()
    {
        var state = new ToolState("call_1", "item_9", "search", contentIndex: 2);
        state.Arguments.Append("{\"q\":");
        state.Arguments.Append("\"x\"}");

        state.CallId.ShouldBe("call_1");
        state.ItemId.ShouldBe("item_9");
        state.Name.ShouldBe("search");
        state.ContentIndex.ShouldBe(2);
        state.Arguments.ToString().ShouldBe("{\"q\":\"x\"}");
    }
}
