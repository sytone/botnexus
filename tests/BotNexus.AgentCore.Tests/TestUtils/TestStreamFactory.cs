using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.AgentCore.Tests.TestUtils;

internal static class TestStreamFactory
{
    public static LlmStream CreateTextResponse(string text, StopReason reason = StopReason.Stop)
    {
        var stream = new LlmStream();
        var message = CreateAssistantMessage(text, reason);
        stream.Push(new StartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new TextDeltaEvent(0, text, message));
        stream.Push(new TextEndEvent(0, text, message));
        stream.Push(new DoneEvent(reason, message));
        stream.End(message);
        return stream;
    }

    public static LlmStream CreateToolCallResponse(
        params (string id, string name, Dictionary<string, object?> args)[] toolCalls)
    {
        var stream = new LlmStream();
        var content = toolCalls.Select(call => (ContentBlock)new ToolCallContent(call.id, call.name, call.args)).ToList();
        var message = CreateAssistantMessage(string.Empty, StopReason.ToolUse, content);

        stream.Push(new StartEvent(message));
        for (var index = 0; index < toolCalls.Length; index++)
        {
            var toolCall = (ToolCallContent)content[index];
            stream.Push(new ToolCallStartEvent(index, message));
            stream.Push(new ToolCallDeltaEvent(index, "{}", message));
            stream.Push(new ToolCallEndEvent(index, toolCall, message));
        }

        stream.Push(new DoneEvent(StopReason.ToolUse, message));
        stream.End(message);
        return stream;
    }

    public static LlmStream CreateErrorResponse(string errorMessage)
    {
        var stream = new LlmStream();
        var message = CreateAssistantMessage(errorMessage, StopReason.Error, [new TextContent(errorMessage)]);
        stream.Push(new StartEvent(message));
        stream.Push(new ErrorEvent(StopReason.Error, message));
        stream.End(message);
        return stream;
    }

    private static AssistantMessage CreateAssistantMessage(
        string text,
        StopReason reason,
        IReadOnlyList<ContentBlock>? content = null)
    {
        var usage = new Usage
        {
            Input = 10,
            Output = 5,
            TotalTokens = 15
        };

        return new AssistantMessage(
            Content: content ?? [new TextContent(text)],
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: usage,
            StopReason: reason,
            ErrorMessage: reason == StopReason.Error ? text : null,
            ResponseId: "response-1",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
}
