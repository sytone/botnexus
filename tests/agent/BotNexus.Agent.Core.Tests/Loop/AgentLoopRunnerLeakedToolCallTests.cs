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
/// Tier 3 of #1698, issue #1709: when a model (notably opus via github-copilot) leaks a tool
/// call as Anthropic invoke/tool_use XML inside the assistant TEXT channel with a finish reason
/// that is NOT <see cref="StopReason.ToolUse"/>, the loop must RECOVER it: parse the leaked XML
/// into a real tool call, promote the finish reason to <see cref="StopReason.ToolUse"/>, strip the
/// XML from the visible text, and dispatch the tool. This is the executing complement to the
/// Tier 1 sanitizer (#1699) which only strips the markup before delivery. Mirrors the truncated
/// tool-call guard precedent (#1666) so a clean tool turn is behaviour-preserving.
/// </summary>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerLeakedToolCallTests
{
    /// <summary>
    /// A recording tool that counts executes and captures the last arguments so a test can
    /// assert a recovered tool call was dispatched with the parsed arguments.
    /// </summary>
    private sealed class RecordingTool : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{ "type": "object", "properties": { "command": { "type": "string" } } }""").RootElement.Clone();
        private int _executeCount;
        public int ExecuteCount => Volatile.Read(ref _executeCount);
        public IReadOnlyDictionary<string, object?>? LastArguments { get; private set; }
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
            LastArguments = arguments;
            return Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
        }
    }

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

    private static AgentContext ContextWithRecordingTool(RecordingTool tool)
        => new(null, [], [tool]);

    /// <summary>
    /// Single leaked invoke block (FinishReason=Stop) is recovered, the tool dispatches once with
    /// the parsed argument, and the leaked XML is stripped from the persisted assistant text.
    /// </summary>
    [Fact]
    public async Task SingleLeakedInvoke_IsRecoveredAndDispatched()
    {
        const string api = "leaked-toolcall-single";
        var tool = new RecordingTool();
        const string leaked = "Listing now.\n<invoke name=\"shell\"><parameter name=\"command\">gh issue list</parameter></invoke>";
        using var _ = RegisterScriptedProvider(
            api,
            TestStreamFactory.CreateTextResponse(leaked, StopReason.Stop),
            TestStreamFactory.CreateTextResponse("Done.", StopReason.Stop));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("list issues")],
            ContextWithRecordingTool(tool),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(1);
        tool.LastArguments.ShouldNotBeNull();
        tool.LastArguments!["command"].ShouldBe("gh issue list");
        result.OfType<ToolResultAgentMessage>().ShouldHaveSingleItem();
        var recovered = result.OfType<AssistantAgentMessage>().First();
        recovered.Content.ShouldNotContain("<invoke");
        recovered.Content.ShouldContain("Listing now.");
    }

    /// <summary>
    /// Multiple leaked invoke blocks in one turn each become a real tool call and dispatch.
    /// </summary>
    [Fact]
    public async Task MultipleLeakedInvokes_AreAllRecoveredAndDispatched()
    {
        const string api = "leaked-toolcall-multiple";
        var tool = new RecordingTool();
        const string leaked = "<invoke name=\"shell\"><parameter name=\"command\">a</parameter></invoke>"
            + "<invoke name=\"shell\"><parameter name=\"command\">b</parameter></invoke>";
        using var _ = RegisterScriptedProvider(
            api,
            TestStreamFactory.CreateTextResponse(leaked, StopReason.Stop),
            TestStreamFactory.CreateTextResponse("Done.", StopReason.Stop));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("run both")],
            ContextWithRecordingTool(tool),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(2);
        result.OfType<ToolResultAgentMessage>().Count().ShouldBe(2);
    }

    /// <summary>
    /// A leaked invoke with no parameters recovers a tool call with empty arguments and dispatches.
    /// </summary>
    [Fact]
    public async Task NoArgLeakedInvoke_IsRecoveredAndDispatched()
    {
        const string api = "leaked-toolcall-noarg";
        var tool = new RecordingTool();
        const string leaked = "Calling.\n<invoke name=\"shell\"></invoke>";
        using var _ = RegisterScriptedProvider(
            api,
            TestStreamFactory.CreateTextResponse(leaked, StopReason.Stop),
            TestStreamFactory.CreateTextResponse("Done.", StopReason.Stop));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("call")],
            ContextWithRecordingTool(tool),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(1);
        tool.LastArguments.ShouldNotBeNull();
        tool.LastArguments!.Count.ShouldBe(0);
    }

    /// <summary>
    /// Malformed leaked XML (open invoke, never closed) is NOT dispatched and does not crash; the
    /// turn settles as a clean text turn and the tool never executes.
    /// </summary>
    [Fact]
    public async Task MalformedLeakedInvoke_IsNotDispatchedAndDoesNotCrash()
    {
        const string api = "leaked-toolcall-malformed";
        var tool = new RecordingTool();
        const string leaked = "Working <invoke name=\"shell\"><parameter name=\"command\">oops";
        using var _ = RegisterScriptedProvider(
            api,
            TestStreamFactory.CreateTextResponse(leaked, StopReason.Stop),
            TestStreamFactory.CreateTextResponse("Done.", StopReason.Stop));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("go")],
            ContextWithRecordingTool(tool),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(0);
        result.OfType<ToolResultAgentMessage>().ShouldBeEmpty();
    }

    /// <summary>
    /// Ordinary prose that mentions tags conversationally (no real invoke block) is untouched and
    /// never triggers a recovery or tool dispatch -- behaviour-preserving.
    /// </summary>
    [Fact]
    public async Task RealProse_IsUntouchedNoDispatch()
    {
        const string api = "leaked-toolcall-prose";
        var tool = new RecordingTool();
        const string prose = "I will not call any tool. The word invoke appears here as prose.";
        using var _ = RegisterScriptedProvider(
            api,
            TestStreamFactory.CreateTextResponse(prose, StopReason.Stop));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("just talk")],
            ContextWithRecordingTool(tool),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(0);
        var msg = result.OfType<AssistantAgentMessage>().Single();
        msg.Content.ShouldBe(prose);
        msg.FinishReason.ShouldBe(StopReason.Stop);
    }
}
