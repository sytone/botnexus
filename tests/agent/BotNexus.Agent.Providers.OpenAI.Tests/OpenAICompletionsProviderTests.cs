using System.Net;
using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;
using BotNexus.Agent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Agent.Providers.OpenAI.Tests;

public class OpenAICompletionsProviderTests
{
    [Fact]
    public void Provider_HasCorrectApiValue()
    {
        var provider = new OpenAICompletionsProvider(
            new HttpClient(), NullLogger<OpenAICompletionsProvider>.Instance);

        provider.Api.ShouldBe("openai-completions");
    }

    [Fact]
    public void StreamSimple_WithNullOptions_DoesNotThrow()
    {
        // StreamSimple should construct options and delegate to Stream.
        // The actual HTTP call will fail, but construction must succeed.
        var provider = new OpenAICompletionsProvider(
            new HttpClient(), NullLogger<OpenAICompletionsProvider>.Instance);

        var model = TestHelpers.MakeModel();
        var context = TestHelpers.MakeContext();

        // StreamSimple returns an LlmStream immediately (HTTP call is async in background)
        var stream = provider.StreamSimple(model, context);

        stream.ShouldNotBeNull();
    }

    [Fact]
    public void ConvertTools_SetsStrictToFalse()
    {
        // #1408: ConvertTools moved to the shared CompletionsStreamEngine in Providers.Core.
        var tool = new Tool(
            "read_file",
            "Read file",
            JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement.Clone());
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = true };

        var converted = CompletionsStreamEngine.ConvertTools([tool], compat);

        converted.ShouldNotBeNull();
        converted[0]!["function"]!["strict"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void ConvertMessages_SkipsEmptyAssistantWithoutToolCalls()
    {
        // #1540: the per-provider OpenAI/Copilot completions converters were unified into the
        // public CompletionsMessageConverter in Providers.Core, so this can call it directly
        // (no reflection / InternalsVisibleTo needed).
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var model = new LlmModel(
            Id: "gpt-4o",
            Name: "GPT-4o",
            Api: "openai-completions",
            Provider: "openai",
            BaseUrl: "https://api.openai.com/v1",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 128000,
            MaxTokens: 32768);
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("start"), timestamp),
            new AssistantMessage(
                Content: [],
                Api: "openai-completions",
                Provider: "openai",
                ModelId: "gpt-4o",
                Usage: Usage.Empty(),
                StopReason: StopReason.Stop,
                ErrorMessage: null,
                ResponseId: null,
                Timestamp: timestamp),
            new UserMessage(new UserMessageContent("next"), timestamp)
        };

        var converted = CompletionsMessageConverter.Convert(
            null, model, messages, new OpenAICompletionsCompat());

        converted.ShouldNotBeNull();
        converted!
            .Select(node => node?["role"]?.GetValue<string>())
            .Where(role => role is not null)
            .ShouldNotContain("assistant");
    }

    [Fact]
    public void MapStopReason_ContentFilter_MapsToErrorWithMessage()
    {
        // #1408: MapStopReason moved to the shared CompletionsStreamEngine in Providers.Core.
        var mapped = CompletionsStreamEngine.MapStopReason("content_filter");

        mapped.StopReason.ShouldBe(StopReason.Error);
        mapped.ErrorMessage.ShouldBe("Content filtered by provider");
    }

    [Fact]
    public void ConvertMessages_NonVisionModel_FiltersImageOnlyUserMessage()
    {
        // #1540: converter unified into the public CompletionsMessageConverter in Providers.Core.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var model = new LlmModel(
            Id: "gpt-4o-mini",
            Name: "GPT-4o-mini",
            Api: "openai-completions",
            Provider: "openai",
            BaseUrl: "https://api.openai.com/v1",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 128000,
            MaxTokens: 32768);
        var userWithOnlyImage = new UserMessage(new UserMessageContent([
            new ImageContent("aGVsbG8=", "image/png")
        ]), timestamp);

        var converted = CompletionsMessageConverter.Convert(
            null, model, new Message[] { userWithOnlyImage }, new OpenAICompletionsCompat());

        converted.ShouldNotBeNull();
        converted.ShouldBeEmpty();
    }

    [Fact]
    public void ConvertMessages_SanitizesSystemUserAndToolResultText()
    {
        // #1540: converter unified into the public CompletionsMessageConverter in Providers.Core.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var model = new LlmModel(
            Id: "gpt-4o",
            Name: "GPT-4o",
            Api: "openai-completions",
            Provider: "openai",
            BaseUrl: "https://api.openai.com/v1",
            Reasoning: true,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 128000,
            MaxTokens: 32768);
        var messages = new Message[]
        {
            new UserMessage(new UserMessageContent("hello \uD800 world"), timestamp),
            new ToolResultMessage(
                ToolCallId: "call_1",
                ToolName: "read_file",
                Content: [new TextContent("tool \uD800 output")],
                IsError: false,
                Timestamp: timestamp)
        };

        var converted = CompletionsMessageConverter.Convert(
            "sys \uD800 prompt", model, messages, new OpenAICompletionsCompat());

        converted.ShouldNotBeNull();
        converted![0]!["content"]!.GetValue<string>().ShouldBe("sys  prompt");
        converted[1]!["content"]!.GetValue<string>().ShouldBe("hello  world");
        converted[2]!["content"]!.GetValue<string>().ShouldBe("tool  output");
    }

    [Theory]
    [InlineData("mistral-small-latest")]
    [InlineData("devstral-small-latest")]
    [InlineData("codestral-latest")]
    [InlineData("pixtral-large-latest")]
    [InlineData("open-mixtral-8x22b")]
    public void ConvertMessages_MistralFamily_NormalizesMatchingToolCallIds(string modelId)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var model = new LlmModel(
            Id: modelId, Name: modelId, Api: "openai-completions", Provider: "custom",
            BaseUrl: "https://example.test/v1", Reasoning: false, Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 32000, MaxTokens: 4096);
        const string originalId = "toolu_01CBhTTz95qkd9LJMdC9sf8t";
        Message[] messages =
        [
            new AssistantMessage(
                [new ToolCallContent(originalId, "read", new Dictionary<string, object?>())],
                "openai-completions", "custom", modelId, Usage.Empty(), StopReason.ToolUse,
                null, null, timestamp),
            new ToolResultMessage(originalId, "read", [new TextContent("ok")], false, timestamp)
        ];

        var converted = CompletionsMessageConverter.Convert(
            null, model, messages, new OpenAICompletionsCompat());

        converted[0]!["tool_calls"]![0]!["id"]!.GetValue<string>().ShouldBe("toolu01CB");
        converted[1]!["tool_call_id"]!.GetValue<string>().ShouldBe("toolu01CB");
    }

    [Fact]
    public void ConvertMessages_NonMistralModel_PreservesToolCallIds()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var model = new LlmModel(
            Id: "qwen-coder", Name: "Qwen", Api: "openai-completions", Provider: "custom",
            BaseUrl: "https://example.test/v1", Reasoning: false, Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 32000, MaxTokens: 4096);
        const string originalId = "call_with-punctuation!";
        Message[] messages =
        [
            new AssistantMessage(
                [new ToolCallContent(originalId, "read", new Dictionary<string, object?>())],
                "openai-completions", "custom", model.Id, Usage.Empty(), StopReason.ToolUse,
                null, null, timestamp),
            new ToolResultMessage(originalId, "read", [new TextContent("ok")], false, timestamp)
        ];

        var converted = CompletionsMessageConverter.Convert(
            null, model, messages, new OpenAICompletionsCompat());

        converted[0]!["tool_calls"]![0]!["id"]!.GetValue<string>().ShouldBe(originalId);
        converted[1]!["tool_call_id"]!.GetValue<string>().ShouldBe(originalId);
    }

    [Fact]
    public void CompletionsMessageConverter_IsUnifiedIntoCore_NotDuplicatedPerProvider()
    {
        // #1540: the OpenAI and Copilot completions converters were near-byte-identical duplicates
        // (enforced only by CopilotCompletionsProviderParityTests). They are now a single shared
        // type in Providers.Core. Lock that here so the duplication cannot silently return.
        var coreAssembly = typeof(CompletionsMessageConverter).Assembly;
        coreAssembly.GetName().Name.ShouldBe("BotNexus.Agent.Providers.Core");

        var openAiAssembly = typeof(OpenAICompletionsProvider).Assembly;
        openAiAssembly.GetType("BotNexus.Agent.Providers.OpenAI.OpenAICompletionsMessageConverter")
            .ShouldBeNull("the OpenAI completions converter must be unified into Providers.Core, not duplicated");
        openAiAssembly.GetType("BotNexus.Agent.Providers.Copilot.Completions.CopilotCompletionsMessageConverter")
            .ShouldBeNull("the Copilot completions converter must be unified into Providers.Core, not duplicated");
    }
}

