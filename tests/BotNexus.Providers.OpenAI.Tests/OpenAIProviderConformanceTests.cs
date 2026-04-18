using System.Text.Json;
using BotNexus.Providers.Conformance.Tests;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.OpenAI;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Providers.OpenAI.Tests;

public sealed class OpenAIProviderConformanceTests : StreamingProviderConformanceTests
{
    protected override IApiProvider CreateProvider(HttpMessageHandler handler) =>
        new OpenAICompletionsProvider(
            new HttpClient(handler),
            NullLogger<OpenAICompletionsProvider>.Instance);

    protected override LlmModel CreateModel() => TestHelpers.MakeModel(reasoning: false);

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
