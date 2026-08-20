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
    [InlineData("cancelled", StopReason.Aborted)]
    [InlineData("in_progress", StopReason.Stop)]
    [InlineData("queued", StopReason.Stop)]
    [InlineData("something_unknown", StopReason.Stop)]
    [InlineData(null, StopReason.Stop)]
    public void MapStopReason_MapsKnownStatusesAndFallsBackToStop(string? status, StopReason expected)
    {
        ResponsesStreamHelpers.MapStopReason(status).ShouldBe(expected);
    }

    /// <summary>
    /// AC1/AC2 of #3294, asserted by name so the cancelled arm cannot be silently folded back into
    /// the error arm: a caller-cancelled Responses turn is an abort, not a provider failure, and it
    /// must normalise identically to the out-of-band <c>ResponsesStreamEngine.EmitAborted</c> path.
    /// </summary>
    [Fact]
    public void MapStopReason_Cancelled_MapsToAborted_NotError()
    {
        ResponsesStreamHelpers.MapStopReason("cancelled").ShouldBe(StopReason.Aborted);
        ResponsesStreamHelpers.MapStopReason("cancelled").ShouldNotBe(StopReason.Error);

        // Sad path: a genuine provider failure stays Error, so the fix does not blur the two.
        ResponsesStreamHelpers.MapStopReason("failed").ShouldBe(StopReason.Error);
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

        // #3297 AC3: this payload reports no reasoning breakdown, so the field stays "not reported".
        usage.Reasoning.ShouldBeNull();
    }

    /// <summary>
    /// #3297 AC3/AC4. The Responses payload carries the breakdown under
    /// <c>output_tokens_details.reasoning_tokens</c>. <c>null</c> means the provider did not report
    /// it; <c>0</c> means it reported zero. The two must stay distinguishable, otherwise an
    /// unreported measurement masquerades as a measured zero.
    /// </summary>
    [Fact]
    public void ParseUsage_ReasoningTokens_NullWhenAbsent_ZeroWhenReportedZero()
    {
        using var absentDoc = JsonDocument.Parse(
            """{ "input_tokens": 10, "output_tokens": 5 }""");
        ResponsesStreamHelpers.ParseUsage(absentDoc.RootElement, Model()).Reasoning.ShouldBeNull();

        // Details object present but without the reasoning key is still "not reported".
        using var noKeyDoc = JsonDocument.Parse(
            """
            {
                "input_tokens": 10,
                "output_tokens": 5,
                "output_tokens_details": { "audio_tokens": 2 }
            }
            """);
        ResponsesStreamHelpers.ParseUsage(noKeyDoc.RootElement, Model()).Reasoning.ShouldBeNull();

        using var zeroDoc = JsonDocument.Parse(
            """
            {
                "input_tokens": 10,
                "output_tokens": 5,
                "output_tokens_details": { "reasoning_tokens": 0 }
            }
            """);
        var zero = ResponsesStreamHelpers.ParseUsage(zeroDoc.RootElement, Model());
        zero.Reasoning.ShouldBe(0);
        zero.Reasoning.ShouldNotBeNull();
    }

    /// <summary>
    /// #3297 AC3/AC5. A reported reasoning count is carried through, and because <c>Output</c> keeps
    /// its inclusive meaning and <c>CalculateCost</c> is untouched, the computed cost is identical to
    /// the same payload without the breakdown.
    /// </summary>
    [Fact]
    public void ParseUsage_ReportedReasoning_IsCarried_AndCostIsUnchanged()
    {
        using var withDoc = JsonDocument.Parse(
            """
            {
                "input_tokens": 100,
                "output_tokens": 45,
                "output_tokens_details": { "reasoning_tokens": 5 }
            }
            """);
        using var withoutDoc = JsonDocument.Parse(
            """{ "input_tokens": 100, "output_tokens": 45 }""");

        var with = ResponsesStreamHelpers.ParseUsage(withDoc.RootElement, Model());
        var without = ResponsesStreamHelpers.ParseUsage(withoutDoc.RootElement, Model());

        with.Reasoning.ShouldBe(5);
        with.Output.ShouldBe(45);
        with.Output.ShouldBe(without.Output);
        with.TotalTokens.ShouldBe(without.TotalTokens);
        with.Cost.ShouldBe(without.Cost);
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
