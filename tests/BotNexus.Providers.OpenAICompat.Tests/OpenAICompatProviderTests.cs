using FluentAssertions;
using BotNexus.Agent.Providers.OpenAICompat;
using System.Reflection;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Providers.OpenAICompat.Tests;

public class OpenAICompatProviderTests
{
    [Fact]
    public void Api_ReturnsOpenAICompat()
    {
        var provider = new OpenAICompatProvider(new HttpClient());

        provider.Api.Should().Be("openai-compat");
    }

    [Fact]
    public void CanConstructProviderInstance()
    {
        var provider = new OpenAICompatProvider(new HttpClient());

        provider.Should().NotBeNull();
        provider.Should().BeAssignableTo<Core.Registry.IApiProvider>();
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
        method.Should().NotBeNull();

        var mapped = ((StopReason StopReason, string? ErrorMessage))method!.Invoke(null, [finishReason, false])!;

        mapped.StopReason.Should().Be(expectedReason);
        mapped.ErrorMessage.Should().Be(expectedError);
    }

    [Fact]
    public void BuildRequestBody_WithToolHistoryAndNoTools_SendsEmptyToolsArray()
    {
        var method = typeof(OpenAICompatProvider).GetMethod(
            "BuildRequestBody",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

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

        body.Should().NotBeNull();
        body!["tools"].Should().NotBeNull();
        body["tools"]!.AsArray().Should().BeEmpty();
    }
}
