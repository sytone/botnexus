using System.Text.Json;
using BotNexus.Providers.Conformance.Tests;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.OpenAICompat;

namespace BotNexus.Agent.Providers.OpenAICompat.Tests;

public sealed class OpenAICompatProviderConformanceTests : StreamingProviderConformanceTests
{
    protected override IApiProvider CreateProvider(HttpMessageHandler handler) =>
        new OpenAICompatProvider(new HttpClient(handler));

    protected override LlmModel CreateModel() => new(
        Id: "compat-model",
        Name: "Compat",
        Api: "openai-compat",
        Provider: "custom",
        BaseUrl: "https://compat.example/v1",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 16384,
        MaxTokens: 4096);

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

    /// <summary>
    /// Two tool calls at wire indices 0 and 1 whose argument fragments arrive interleaved across
    /// frames. The completions stream processor keys its block state by tool-call index, so a
    /// regression to a single "current block" cursor would emit a delta for an index it had already
    /// closed - the breach the #3300 ordering validator reports.
    /// </summary>
    protected override string BuildInterleavedToolCallPayload(
        string firstToolCallId,
        string firstToolName,
        string firstArgumentsJson,
        string secondToolCallId,
        string secondToolName,
        string secondArgumentsJson,
        string providerStopReason)
    {
        var firstHalf = firstArgumentsJson[..(firstArgumentsJson.Length / 2)];
        var firstRest = firstArgumentsJson[(firstArgumentsJson.Length / 2)..];
        var secondHalf = secondArgumentsJson[..(secondArgumentsJson.Length / 2)];
        var secondRest = secondArgumentsJson[(secondArgumentsJson.Length / 2)..];

        return JoinLines(
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
                                    id = firstToolCallId,
                                    type = "function",
                                    function = new { name = firstToolName, arguments = firstHalf }
                                }
                            }
                        }
                    }
                }
            }),
            Data(new
            {
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
                                    index = 1,
                                    id = secondToolCallId,
                                    type = "function",
                                    function = new { name = secondToolName, arguments = secondHalf }
                                }
                            }
                        }
                    }
                }
            }),
            Data(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new object[]
                            {
                                new { index = 0, function = new { arguments = firstRest } }
                            }
                        }
                    }
                }
            }),
            Data(new
            {
                choices = new[]
                {
                    new
                    {
                        delta = new
                        {
                            tool_calls = new object[]
                            {
                                new { index = 1, function = new { arguments = secondRest } }
                            }
                        }
                    }
                }
            }),
            Data(new { choices = new[] { new { finish_reason = providerStopReason, delta = new { } } } }),
            "data: [DONE]");
    }

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
