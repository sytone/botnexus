using BotNexus.Agent.Providers.OpenAICompat;
using System.Reflection;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.OpenAICompat.Tests;

public class OpenAICompatProviderTests
{
    [Fact]
    public void Api_ReturnsOpenAICompat()
    {
        var provider = new OpenAICompatProvider(new HttpClient());

        provider.Api.ShouldBe("openai-compat");
    }

    [Fact]
    public void CanConstructProviderInstance()
    {
        var provider = new OpenAICompatProvider(new HttpClient());

        provider.ShouldNotBeNull();
        provider.ShouldBeAssignableTo<Core.Registry.IApiProvider>();
    }

    [Theory]
    [InlineData("end", StopReason.Stop, null)]
    [InlineData("function_call", StopReason.ToolUse, null)]
    [InlineData("content_filter", StopReason.Error, "Provider finish_reason: content_filter")]
    [InlineData("network_error", StopReason.Error, "Provider finish_reason: network_error")]
    public void MapStopReason_MapsExtendedFinishReasons(string finishReason, StopReason expectedReason, string? expectedError)
    {
        var method = typeof(OpenAICompatProvider).GetMethod(
            "MapStopReason",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        var mapped = ((StopReason StopReason, string? ErrorMessage))method!.Invoke(null, [finishReason, false])!;

        mapped.StopReason.ShouldBe(expectedReason);
        mapped.ErrorMessage.ShouldBe(expectedError);
    }

    [Theory]
    [InlineData("mistral-small-latest")]
    [InlineData("devstral-small-latest")]
    [InlineData("codestral-latest")]
    [InlineData("pixtral-large-latest")]
    [InlineData("open-mixtral-8x22b")]
    public void BuildRequestBody_MistralFamily_NormalizesMatchingToolCallIds(string modelId)
    {
        var method = typeof(OpenAICompatProvider).GetMethod(
            "BuildRequestBody", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        var model = new LlmModel(
            Id: modelId, Name: modelId, Api: "openai-compat", Provider: "custom",
            BaseUrl: "https://example.test/v1", Reasoning: false, Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 32000, MaxTokens: 4096);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const string originalId = "toolu_01CBhTTz95qkd9LJMdC9sf8t";
        var context = new Context(null,
        [
            new AssistantMessage(
                [new ToolCallContent(originalId, "read", new Dictionary<string, object?>())],
                "openai-compat", "custom", modelId, Usage.Empty(), StopReason.ToolUse,
                null, null, timestamp),
            new ToolResultMessage(originalId, "read", [new TextContent("ok")], false, timestamp)
        ], null);

        var body = method!.Invoke(
            null, [model, context, null, new OpenAICompletionsCompat()]) as JsonObject;
        var messages = body!["messages"]!.AsArray();

        messages[0]!["tool_calls"]![0]!["id"]!.GetValue<string>().ShouldBe("toolu01CB");
        messages[1]!["tool_call_id"]!.GetValue<string>().ShouldBe("toolu01CB");
    }

    [Fact]
    public void BuildRequestBody_NonMistralModel_PreservesToolCallIds()
    {
        var method = typeof(OpenAICompatProvider).GetMethod(
            "BuildRequestBody", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();
        var model = new LlmModel(
            Id: "qwen-coder", Name: "Qwen", Api: "openai-compat", Provider: "custom",
            BaseUrl: "https://example.test/v1", Reasoning: false, Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0), ContextWindow: 32000, MaxTokens: 4096);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const string originalId = "call_with-punctuation!";
        var context = new Context(null,
        [
            new AssistantMessage(
                [new ToolCallContent(originalId, "read", new Dictionary<string, object?>())],
                "openai-compat", "custom", model.Id, Usage.Empty(), StopReason.ToolUse,
                null, null, timestamp),
            new ToolResultMessage(originalId, "read", [new TextContent("ok")], false, timestamp)
        ], null);

        var body = method!.Invoke(
            null, [model, context, null, new OpenAICompletionsCompat()]) as JsonObject;
        var messages = body!["messages"]!.AsArray();

        messages[0]!["tool_calls"]![0]!["id"]!.GetValue<string>().ShouldBe(originalId);
        messages[1]!["tool_call_id"]!.GetValue<string>().ShouldBe(originalId);
    }

    [Fact]
    public void BuildRequestBody_WithToolHistoryAndNoTools_SendsEmptyToolsArray()
    {
        var method = typeof(OpenAICompatProvider).GetMethod(
            "BuildRequestBody",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        var model = new LlmModel(
            Id: "gpt-4o-mini",
            Name: "GPT-4o Mini",
            Api: "openai-compat",
            Provider: "openai",
            BaseUrl: "https://api.openai.com/v1",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 128000,
            MaxTokens: 32768);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var context = new Context(
            SystemPrompt: null,
            Messages:
            [
                new AssistantMessage(
                    Content: [new ToolCallContent("call_1", "search", new Dictionary<string, object?>())],
                    Api: "openai-compat",
                    Provider: "openai",
                    ModelId: "gpt-4o-mini",
                    Usage: Usage.Empty(),
                    StopReason: StopReason.ToolUse,
                    ErrorMessage: null,
                    ResponseId: null,
                    Timestamp: timestamp)
            ],
            Tools: null);

        var body = method!.Invoke(
            null,
            [model, context, null, new OpenAICompletionsCompat()]) as JsonObject;

        body.ShouldNotBeNull();
        body!["tools"].ShouldNotBeNull();
        body["tools"]!.AsArray().ShouldBeEmpty();
    }
}
