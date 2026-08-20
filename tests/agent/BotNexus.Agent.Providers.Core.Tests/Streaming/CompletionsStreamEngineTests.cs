using System.Text.Json;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Unit coverage for the shared Chat Completions engine that both the OpenAI and Copilot Completions
/// providers collapse onto (step 6/6 of #1377). The provider-level
/// <c>CopilotCompletionsProviderParityTests</c> remain the byte-identical wire-contract safety net;
/// these tests pin the engine's pure helpers and emit shapes directly.
/// </summary>
public class CompletionsStreamEngineTests
{
    private static LlmModel Model(string provider = "openai") => new(
        Id: "gpt-4o",
        Name: "GPT-4o",
        Api: "openai-completions",
        Provider: provider,
        BaseUrl: "https://api.openai.com/v1",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(1.0m, 2.0m, 0.5m, 1.5m),
        ContextWindow: 128000,
        MaxTokens: 32768);

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Fact]
    public void ConvertTools_WithStrictModeSupported_EmitsStrictFalse()
    {
        var tool = new Tool(
            "read_file",
            "Read file",
            Json("""{"type":"object","properties":{"path":{"type":"string"}}}"""));
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = true };

        var converted = CompletionsStreamEngine.ConvertTools([tool], compat);

        converted.Count.ShouldBe(1);
        converted[0]!["type"]!.GetValue<string>().ShouldBe("function");
        converted[0]!["function"]!["name"]!.GetValue<string>().ShouldBe("read_file");
        converted[0]!["function"]!["strict"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void ConvertTools_WithStrictModeUnsupported_OmitsStrict()
    {
        var tool = new Tool("t", "d", Json("""{"type":"object"}"""));
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = false };

        var converted = CompletionsStreamEngine.ConvertTools([tool], compat);

        converted[0]!["function"]!.AsObject().ContainsKey("strict").ShouldBeFalse();
    }

    [Theory]
    [InlineData("stop", StopReason.Stop, null)]
    [InlineData("end", StopReason.Stop, null)]
    [InlineData("length", StopReason.Length, null)]
    [InlineData("function_call", StopReason.ToolUse, null)]
    [InlineData("tool_calls", StopReason.ToolUse, null)]
    [InlineData("refusal", StopReason.Refusal, null)]
    [InlineData("content_filter", StopReason.Sensitive, "Content filtered by provider")]
    [InlineData("network_error", StopReason.Error, "Provider finish_reason: network_error")]
    [InlineData(null, StopReason.Stop, null)]
    public void MapStopReason_MapsKnownReasons(string? reason, StopReason expected, string? expectedMessage)
    {
        var (stopReason, message) = CompletionsStreamEngine.MapStopReason(reason);
        stopReason.ShouldBe(expected);
        message.ShouldBe(expectedMessage);
    }

    [Fact]
    public void MapStopReason_UnknownReason_MapsToErrorWithRawReason()
    {
        var (stopReason, message) = CompletionsStreamEngine.MapStopReason("weird_reason");
        stopReason.ShouldBe(StopReason.Error);
        message.ShouldBe("Provider finish_reason: weird_reason");
    }

    [Fact]
    public void ParseUsage_FoldsCacheTokensOutOfInputAndAddsReasoningToOutput()
    {
        var usage = CompletionsStreamEngine.ParseUsage(
            Json("""
                {
                  "prompt_tokens": 100,
                  "completion_tokens": 40,
                  "prompt_tokens_details": { "cached_tokens": 30, "cache_write_tokens": 10 },
                  "completion_tokens_details": { "reasoning_tokens": 5 }
                }
                """),
            Usage.Empty(),
            Model());

        // cacheRead = 30 - 10 = 20, cacheWrite = 10, input = 100 - 20 - 10 = 70, output = 40 + 5 = 45
        usage.CacheWrite.ShouldBe(10);
        usage.CacheRead.ShouldBe(20);
        usage.Input.ShouldBe(70);
        usage.Output.ShouldBe(45);
        usage.TotalTokens.ShouldBe(70 + 45 + 20 + 10);

        // #3297 AC2: reasoning is additionally attributed, while Output keeps its inclusive meaning.
        usage.Reasoning.ShouldBe(5);
    }

    /// <summary>
    /// #3297 AC2/AC4. The distinction is load-bearing: <c>null</c> means the provider never reported
    /// reasoning tokens, <c>0</c> means it reported zero. Coercing absent to zero would present a
    /// missing measurement as a measured one and rank a thinking-heavy model as free.
    /// </summary>
    [Fact]
    public void ParseUsage_ReasoningTokens_NullWhenAbsent_ZeroWhenReportedZero()
    {
        var absent = CompletionsStreamEngine.ParseUsage(
            Json("""{ "prompt_tokens": 50, "completion_tokens": 12 }"""),
            Usage.Empty(),
            Model());

        absent.Reasoning.ShouldBeNull();

        // completion_tokens_details present but without reasoning_tokens is still "not reported".
        var detailsWithoutReasoning = CompletionsStreamEngine.ParseUsage(
            Json("""
                {
                  "prompt_tokens": 50,
                  "completion_tokens": 12,
                  "completion_tokens_details": { "audio_tokens": 3 }
                }
                """),
            Usage.Empty(),
            Model());

        detailsWithoutReasoning.Reasoning.ShouldBeNull();

        var reportedZero = CompletionsStreamEngine.ParseUsage(
            Json("""
                {
                  "prompt_tokens": 50,
                  "completion_tokens": 12,
                  "completion_tokens_details": { "reasoning_tokens": 0 }
                }
                """),
            Usage.Empty(),
            Model());

        reportedZero.Reasoning.ShouldBe(0);
        reportedZero.Reasoning.ShouldNotBeNull();
    }

    /// <summary>
    /// #3297 AC5. Reasoning is attribution only: adding the field must not perturb the computed cost
    /// for any payload, whether reasoning is absent, zero, or non-zero.
    /// </summary>
    [Fact]
    public void ParseUsage_ReasoningAttribution_DoesNotChangeComputedCost()
    {
        var withReasoning = CompletionsStreamEngine.ParseUsage(
            Json("""
                {
                  "prompt_tokens": 100,
                  "completion_tokens": 40,
                  "completion_tokens_details": { "reasoning_tokens": 5 }
                }
                """),
            Usage.Empty(),
            Model());

        // The same wire totals expressed without the reasoning breakdown must cost the same,
        // because Output stays inclusive and CalculateCost is untouched.
        var equivalent = CompletionsStreamEngine.ParseUsage(
            Json("""{ "prompt_tokens": 100, "completion_tokens": 45 }"""),
            Usage.Empty(),
            Model());

        withReasoning.Output.ShouldBe(equivalent.Output);
        withReasoning.Cost.ShouldBe(equivalent.Cost);
        withReasoning.Reasoning.ShouldBe(5);
        equivalent.Reasoning.ShouldBeNull();
    }

    [Fact]
    public void ParseUsage_WithoutDetails_UsesPromptAndCompletionTokens()
    {
        var usage = CompletionsStreamEngine.ParseUsage(
            Json("""{ "prompt_tokens": 50, "completion_tokens": 12 }"""),
            Usage.Empty(),
            Model());

        usage.Input.ShouldBe(50);
        usage.Output.ShouldBe(12);
        usage.CacheRead.ShouldBe(0);
        usage.CacheWrite.ShouldBe(0);
    }

    [Fact]
    public void MapThinkingLevel_WithCompatOverride_UsesMappedValue()
    {
        var compat = new OpenAICompletionsCompat
        {
            ReasoningEffortMap = new Dictionary<ThinkingLevel, string> { [ThinkingLevel.High] = "ultra" }
        };

        CompletionsStreamEngine.MapThinkingLevel(ThinkingLevel.High, compat).ShouldBe("ultra");
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, "low")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "medium")]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.ExtraHigh, "xhigh")]
    public void MapThinkingLevel_WithoutOverride_UsesDefaultMapping(ThinkingLevel level, string expected)
    {
        CompletionsStreamEngine.MapThinkingLevel(level, null).ShouldBe(expected);
    }

    [Fact]
    public void ExtractProviderErrorMessage_PlainError_ReturnsMessage()
    {
        var message = CompletionsStreamEngine.ExtractProviderErrorMessage(
            """{ "error": { "message": "boom" } }""", Model());
        message.ShouldBe("boom");
    }

    [Fact]
    public void ExtractProviderErrorMessage_OpenRouterWithMetadata_AppendsCodeAndProvider()
    {
        var message = CompletionsStreamEngine.ExtractProviderErrorMessage(
            """{ "error": { "message": "rate limited", "metadata": { "code": "429", "provider_name": "anthropic" } } }""",
            Model(provider: "openrouter"));
        message.ShouldBe("rate limited (429, anthropic)");
    }

    [Fact]
    public void ExtractProviderErrorMessage_EmptyBody_ReturnsUnknown()
    {
        CompletionsStreamEngine.ExtractProviderErrorMessage("", Model()).ShouldBe("Unknown API error");
    }

    [Fact]
    public void ExtractProviderErrorMessage_NonJson_ReturnsRaw()
    {
        CompletionsStreamEngine.ExtractProviderErrorMessage("not json", Model()).ShouldBe("not json");
    }

    [Fact]
    public async Task EmitError_PushesErrorEventAndEndsWithErrorMessage()
    {
        var stream = new LlmStream();
        CompletionsStreamEngine.EmitError(stream, "openai-completions", Model(), "kaboom");

        var result = await stream.GetResultAsync();
        result.StopReason.ShouldBe(StopReason.Error);
        result.ErrorMessage.ShouldBe("kaboom");
        result.Api.ShouldBe("openai-completions");
    }

    [Fact]
    public async Task EmitAborted_PushesDoneEventAndEndsWithCancelledMessage()
    {
        var stream = new LlmStream();
        CompletionsStreamEngine.EmitAborted(stream, "github-copilot-completions", Model());

        var result = await stream.GetResultAsync();
        result.StopReason.ShouldBe(StopReason.Aborted);
        result.ErrorMessage.ShouldBe("Request was cancelled");
        result.Api.ShouldBe("github-copilot-completions");
    }
}
