using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Providers.Core.Compatibility;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.OpenAI;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Providers.OpenAI.Tests;

public class OpenAICompletionsProviderTests
{
    [Fact]
    public void Provider_HasCorrectApiValue()
    {
        var provider = new OpenAICompletionsProvider(
            new HttpClient(), NullLogger<OpenAICompletionsProvider>.Instance);

        provider.Api.Should().Be("openai-completions");
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

        stream.Should().NotBeNull();
    }

    [Fact]
    public void ConvertTools_SetsStrictToFalse()
    {
        var convertTools = typeof(OpenAICompletionsProvider).GetMethod(
            "ConvertTools",
            BindingFlags.NonPublic | BindingFlags.Static);
        convertTools.Should().NotBeNull();

        var tool = new Tool(
            "read_file",
            "Read file",
            JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement.Clone());
        var compat = new OpenAICompletionsCompat { SupportsStrictMode = true };

        var converted = convertTools!.Invoke(null, [new List<Tool> { tool }, compat]) as JsonArray;

        converted.Should().NotBeNull();
        converted![0]!["function"]!["strict"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void ConvertMessages_SkipsEmptyAssistantWithoutToolCalls()
    {
        var convertMessages = typeof(OpenAICompletionsProvider).GetMethod(
            "ConvertMessages",
            BindingFlags.NonPublic | BindingFlags.Static);
        convertMessages.Should().NotBeNull();

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

        converted.Should().NotBeNull();
        converted!
            .Select(node => node?["role"]?.GetValue<string>())
            .Where(role => role is not null)
            .Should()
            .NotContain("assistant");
    }
}
