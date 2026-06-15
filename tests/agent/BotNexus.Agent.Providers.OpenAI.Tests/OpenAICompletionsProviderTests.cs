using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;
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
        var convertTools = typeof(OpenAICompletionsProvider).GetMethod(
            "ConvertTools",
            BindingFlags.NonPublic | BindingFlags.Static);
        convertTools.ShouldNotBeNull();

        var tool = new Tool(
            "read_file",
            "Read file",
            JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement.Clone());
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = true };

        var converted = convertTools!.Invoke(null, [new List<Tool> { tool }, compat]) as JsonArray;

        converted.ShouldNotBeNull();
        converted![0]!["function"]!["strict"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void ConvertMessages_SkipsEmptyAssistantWithoutToolCalls()
    {
        // #1405: ConvertMessages moved to the internal OpenAICompletionsMessageConverter.
        // Resolve it by name from the provider assembly so the test needs no InternalsVisibleTo.
        var converterType = typeof(OpenAICompletionsProvider).Assembly.GetType(
            "BotNexus.Agent.Providers.OpenAI.OpenAICompletionsMessageConverter");
        converterType.ShouldNotBeNull();
        var convertMessages = converterType!.GetMethod(
            "Convert",
            BindingFlags.NonPublic | BindingFlags.Static);
        convertMessages.ShouldNotBeNull();

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

        var converted = convertMessages!.Invoke(
            null,
            [null, model, messages, new OpenAICompletionsCompat()]) as JsonArray;

        converted.ShouldNotBeNull();
        converted!
            .Select(node => node?["role"]?.GetValue<string>())
            .Where(role => role is not null)
            .ShouldNotContain("assistant");
    }

    [Fact]
    public void MapStopReason_ContentFilter_MapsToErrorWithMessage()
    {
        var mapStopReason = typeof(OpenAICompletionsProvider).GetMethod(
            "MapStopReason",
            BindingFlags.NonPublic | BindingFlags.Static);
        mapStopReason.ShouldNotBeNull();

        var mapped = ((StopReason StopReason, string? ErrorMessage))mapStopReason!.Invoke(null, ["content_filter"])!;

        mapped.StopReason.ShouldBe(StopReason.Error);
        mapped.ErrorMessage.ShouldBe("Content filtered by provider");
    }

    [Fact]
    public void ConvertMessages_NonVisionModel_FiltersImageOnlyUserMessage()
    {
        // #1405: ConvertMessages moved to the internal OpenAICompletionsMessageConverter.
        // Resolve it by name from the provider assembly so the test needs no InternalsVisibleTo.
        var converterType = typeof(OpenAICompletionsProvider).Assembly.GetType(
            "BotNexus.Agent.Providers.OpenAI.OpenAICompletionsMessageConverter");
        converterType.ShouldNotBeNull();
        var convertMessages = converterType!.GetMethod(
            "Convert",
            BindingFlags.NonPublic | BindingFlags.Static);
        convertMessages.ShouldNotBeNull();

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

        var converted = convertMessages!.Invoke(
            null,
            [null, model, new Message[] { userWithOnlyImage }, new OpenAICompletionsCompat()]) as JsonArray;

        converted.ShouldNotBeNull();
        converted.ShouldBeEmpty();
    }

    [Fact]
    public void ConvertMessages_SanitizesSystemUserAndToolResultText()
    {
        // #1405: ConvertMessages moved to the internal OpenAICompletionsMessageConverter.
        // Resolve it by name from the provider assembly so the test needs no InternalsVisibleTo.
        var converterType = typeof(OpenAICompletionsProvider).Assembly.GetType(
            "BotNexus.Agent.Providers.OpenAI.OpenAICompletionsMessageConverter");
        converterType.ShouldNotBeNull();
        var convertMessages = converterType!.GetMethod(
            "Convert",
            BindingFlags.NonPublic | BindingFlags.Static);
        convertMessages.ShouldNotBeNull();

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

        var converted = convertMessages!.Invoke(
            null,
            ["sys \uD800 prompt", model, messages, new OpenAICompletionsCompat()]) as JsonArray;

        converted.ShouldNotBeNull();
        converted![0]!["content"]!.GetValue<string>().ShouldBe("sys  prompt");
        converted[1]!["content"]!.GetValue<string>().ShouldBe("hello  world");
        converted[2]!["content"]!.GetValue<string>().ShouldBe("tool  output");
    }
}

