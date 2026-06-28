using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Regression tests for #1666: a truncated assistant turn (provider stop reason
/// <see cref="StopReason.Length"/>, a content filter, or stream EOF) that nonetheless
/// surfaces a parsed tool call must NOT be dispatched. Tool dispatch is gated on a
/// <see cref="StopReason.ToolUse"/> terminal, so a half-formed tool call cut off at the
/// token limit is never executed with incomplete/garbled arguments.
/// </summary>
/// <remarks>
/// BotNexus analogue of the OpenClaw fix that ignores truncated tool calls. Every BotNexus
/// provider promotes a legitimate, complete tool call to <see cref="StopReason.ToolUse"/>
/// at the parser/provider layer (Anthropic/Copilot native tool_use, OpenAI tool_calls,
/// Responses promotes a complete call from Stop), so the loop-level guard
/// (<c>FinishReason == ToolUse</c>) blocks only the truncated case and never a real tool
/// turn.
/// </remarks>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerTruncatedToolCallTests
{
    /// <summary>
    /// A recording tool that counts how many times <see cref="ExecuteAsync"/> is invoked,
    /// so a test can assert a truncated turn's tool call is never dispatched.
    /// </summary>
    private sealed class RecordingTool : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{ "type": "object", "properties": { "command": { "type": "string" } } }""").RootElement.Clone();

        private int _executeCount;

        public int ExecuteCount => Volatile.Read(ref _executeCount);

        public string Name => "shell";

        public string Label => "Shell";

        public Tool Definition => new("shell", "Run a shell command", Schema);

        public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default)
            => Task.FromResult(arguments);

        public Task<AgentToolResult> ExecuteAsync(
            string toolCallId,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default,
            AgentToolUpdateCallback? onUpdate = null)
        {
            Interlocked.Increment(ref _executeCount);
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
        }
    }

    /// <summary>
    /// Registers a scripted provider whose responses are returned in sequence on each
    /// successive LLM call (call 1 = first entry, call 2 = second, ...). The last entry is
    /// reused for any further calls.
    /// </summary>
    private static IDisposable RegisterScriptedProvider(string apiId, params LlmStream[] responses)
    {
        var index = -1;
        return TestHelpers.RegisterProvider(
            new TestApiProvider(apiId, simpleStreamFactory: (_, _, _) =>
            {
                var next = Interlocked.Increment(ref index);
                var slot = Math.Min(next, responses.Length - 1);
                return responses[slot];
            }));
    }

    /// <summary>
    /// Builds an assistant stream whose message carries narration text followed by a tool
    /// call, terminating with the supplied stop reason. Mirrors a model turn that was cut
    /// off mid-tool-call: the content blocks contain a tool call but the terminal is
    /// <see cref="StopReason.Length"/> (not <see cref="StopReason.ToolUse"/>).
    /// </summary>
    private static LlmStream CreateTextPlusToolCallResponse(
        StopReason reason,
        string text,
        (string id, string name, Dictionary<string, object?> args) toolCall)
    {
        var stream = new LlmStream();
        var toolCallContent = new ToolCallContent(toolCall.id, toolCall.name, toolCall.args);
        var content = new List<ContentBlock> { new TextContent(text), toolCallContent };
        var message = new AssistantMessage(
            Content: content,
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: new Usage { Input = 10, Output = 5, TotalTokens = 15 },
            StopReason: reason,
            ErrorMessage: null,
            ResponseId: "response-1",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new StartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new TextDeltaEvent(0, text, message));
        stream.Push(new TextEndEvent(0, text, message));
        stream.Push(new ToolCallStartEvent(1, message));
        stream.Push(new ToolCallDeltaEvent(1, "{\"command\":\"gh issue cr", message));
        stream.Push(new ToolCallEndEvent(1, toolCallContent, message));
        stream.Push(new DoneEvent(reason, message));
        stream.End(message);
        return stream;
    }

    private static AgentContext ContextWithRecordingTool(RecordingTool tool)
        => new(null, [], [tool]);

    /// <summary>
    /// The core #1666 reproduction: a turn whose terminal is <see cref="StopReason.Length"/>
    /// surfaces a (partial) tool call. The loop must NOT dispatch it -- the recording tool's
    /// execute is never called -- and the persisted assistant message must not carry the
    /// tool call (its visible text and finish reason are retained).
    /// </summary>
    [Fact]
    public async Task TruncatedToolCallTurn_IsNotDispatchedAndIsStrippedFromPersistedMessage()
    {
        const string api = "truncated-toolcall-length";
        var tool = new RecordingTool();
        using var _ = RegisterScriptedProvider(
            api,
            // Turn 1: truncated at the token limit while emitting a tool call.
            CreateTextPlusToolCallResponse(
                StopReason.Length,
                "Filing the issue now",
                ("call-1", "shell", new Dictionary<string, object?> { ["command"] = "gh issue cr" })),
            // Turn 2: a benign completion so the loop terminates instead of looping on the
            // reused truncated stream when the guard is (incorrectly) absent.
            TestStreamFactory.CreateTextResponse("Done.", StopReason.Stop));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("file the issue")],
            ContextWithRecordingTool(tool),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        // The truncated tool call must never have executed.
        tool.ExecuteCount.ShouldBe(0);

        // The first assistant message (the truncated turn) keeps its text and Length finish
        // reason but must not carry the tool call that a later code path could re-dispatch.
        var truncatedAssistant = result
            .OfType<AssistantAgentMessage>()
            .First(m => m.FinishReason == StopReason.Length);
        truncatedAssistant.Content.ShouldContain("Filing the issue now");
        truncatedAssistant.ToolCalls.ShouldBeNull();
        (truncatedAssistant.ContentBlocks ?? []).OfType<ToolCallContent>().ShouldBeEmpty();

        // No tool result message was ever produced for the truncated call.
        result.OfType<ToolResultAgentMessage>().ShouldBeEmpty();
    }

    /// <summary>
    /// A normal <see cref="StopReason.ToolUse"/> turn with a complete tool call still
    /// dispatches -- the guard must not regress legitimate tool execution.
    /// </summary>
    [Fact]
    public async Task CompleteToolUseTurn_IsDispatched()
    {
        const string api = "truncated-toolcall-tooluse";
        var tool = new RecordingTool();
        using var _ = RegisterScriptedProvider(
            api,
            // Turn 1: a legitimate, complete tool call (ToolUse terminal) -> must dispatch.
            TestStreamFactory.CreateToolCallResponse(
                ("call-1", "shell", new Dictionary<string, object?> { ["command"] = "gh issue list" })),
            // Turn 2: benign completion so the run settles after the tool result feeds back.
            TestStreamFactory.CreateTextResponse("All set.", StopReason.Stop));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("list the issues")],
            ContextWithRecordingTool(tool),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(1);
        result.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem();
    }
}
