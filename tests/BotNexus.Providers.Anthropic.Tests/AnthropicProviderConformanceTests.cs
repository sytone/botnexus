using System.Text.Json;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Providers.Conformance.Tests;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Providers.Anthropic.Tests;

public sealed class AnthropicProviderConformanceTests : StreamingProviderConformanceTests
{
    protected override IApiProvider CreateProvider(HttpMessageHandler handler) =>
        new AnthropicProvider(new HttpClient(handler));

    protected override LlmModel CreateModel() => TestHelpers.MakeModel();

    protected override string BuildTextPayload(string text, string providerStopReason)
    {
        var encodedText = JsonSerializer.Serialize(text);
        return JoinLines(
            "event: message_start",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}",
            "event: content_block_start",
            "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\"}}",
            "event: content_block_delta",
            $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"text_delta\",\"text\":{encodedText}}}}}",
            "event: content_block_stop",
            "data: {\"type\":\"content_block_stop\",\"index\":0}",
            "event: message_delta",
            $"data: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{providerStopReason}\"}}}}",
            "event: message_stop",
            "data: {\"type\":\"message_stop\"}");
    }

    protected override string BuildToolCallPayload(
        string toolCallId,
        string toolName,
        string argumentsJson,
        string providerStopReason)
    {
        var encodedArgs = JsonSerializer.Serialize(argumentsJson);
        return JoinLines(
            "event: message_start",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}",
            "event: content_block_start",
            $"data: {{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{{\"type\":\"tool_use\",\"id\":\"{toolCallId}\",\"name\":\"{toolName}\"}}}}",
            "event: content_block_delta",
            $"data: {{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{{\"type\":\"input_json_delta\",\"partial_json\":{encodedArgs}}}}}",
            "event: content_block_stop",
            "data: {\"type\":\"content_block_stop\",\"index\":0}",
            "event: message_delta",
            $"data: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{providerStopReason}\"}}}}",
            "event: message_stop",
            "data: {\"type\":\"message_stop\"}");
    }

    protected override string BuildFinishReasonPayload(string providerStopReason) =>
        JoinLines(
            "event: message_start",
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\"}}",
            "event: message_delta",
            $"data: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{providerStopReason}\"}}}}",
            "event: message_stop",
            "data: {\"type\":\"message_stop\"}");

    protected override string BuildUsagePayload(int inputTokens, int outputTokens, string providerStopReason) =>
        JoinLines(
            "event: message_start",
            $"data: {{\"type\":\"message_start\",\"message\":{{\"id\":\"msg_1\",\"usage\":{{\"input_tokens\":{inputTokens}}}}}}}",
            "event: message_delta",
            $"data: {{\"type\":\"message_delta\",\"delta\":{{\"stop_reason\":\"{providerStopReason}\"}},\"usage\":{{\"output_tokens\":{outputTokens}}}}}",
            "event: message_stop",
            "data: {\"type\":\"message_stop\"}");

    protected override string MapCanonicalStopReason(string canonicalReason) => canonicalReason switch
    {
        "stop" => "end_turn",
        "length" => "max_tokens",
        "tool_use" => "tool_use",
        _ => throw new ArgumentOutOfRangeException(nameof(canonicalReason), canonicalReason, null)
    };

    private static string JoinLines(params string[] lines) => string.Join('\n', lines);
}
