using System.Text.Json;
using BotNexus.Providers.Conformance.Tests;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Providers.Copilot.Tests;

public sealed class CopilotProviderConformanceTests : StreamingProviderConformanceTests
{
    protected override IApiProvider CreateProvider(HttpMessageHandler handler) =>
        new OpenAICompletionsProvider(
            new HttpClient(handler),
            NullLogger<OpenAICompletionsProvider>.Instance);

    protected override LlmModel CreateModel() => new(
        Id: "gpt-4o",
        Name: "GPT-4o (Copilot)",
        Api: "openai-completions",
        Provider: "github-copilot",
        BaseUrl: "https://api.githubcopilot.com",
        Reasoning: false,
        Input: ["text", "image"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 128000,
        MaxTokens: 16384);

    protected override string BuildTextPayload(string text, string providerStopReason)
        => JoinLines(
            Data(new { id = "resp_1", choices = new[] { new { delta = new { content = text } } } }),
            Data(new { choices = new[] { new { finish_reason = providerStopReason, delta = new { } } } }),
            "data: [DONE]");

    protected override string BuildToolCallPayload(
        string toolCallId,
        string toolName,
        string argumentsJson,
        string providerStopReason)
        => JoinLines(
            Data(new
            {
                id = "resp_1",
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new object[]
                            {
                                new
                                {
                                    index = 0,
                                    id = toolCallId,
                                    type = "function",
                                    function = new { name = toolName, arguments = argumentsJson }
                                }
                            }
                        }
                    }
                }
            }),
            Data(new { choices = new[] { new { finish_reason = providerStopReason, delta = new { } } } }),
            "data: [DONE]");

    protected override string BuildFinishReasonPayload(string providerStopReason) =>
        JoinLines(
            Data(new { id = "resp_1", choices = new[] { new { delta = new { content = "ok" } } } }),
            Data(new { choices = new[] { new { finish_reason = providerStopReason, delta = new { } } } }),
            "data: [DONE]");

    protected override string BuildUsagePayload(int inputTokens, int outputTokens, string providerStopReason)
    {
        var totalTokens = inputTokens + outputTokens;
        return JoinLines(
            Data(new
            {
                id = "resp_1",
                usage = new { prompt_tokens = inputTokens, completion_tokens = outputTokens, total_tokens = totalTokens },
                choices = new[] { new { delta = new { content = "counted" } } }
            }),
            Data(new { choices = new[] { new { finish_reason = providerStopReason, delta = new { } } } }),
            "data: [DONE]");
    }

    protected override string MapCanonicalStopReason(string canonicalReason) => canonicalReason switch
    {
        "stop" => "stop",
        "length" => "length",
        "tool_use" => "tool_calls",
        _ => throw new ArgumentOutOfRangeException(nameof(canonicalReason), canonicalReason, null)
    };

    private static string JoinLines(params string[] lines) => string.Join('\n', lines);

    private static string Data(object payload) => "data: " + JsonSerializer.Serialize(payload);
}
