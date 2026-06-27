using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Diagnostics;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Regression tests for #1661: the post-turn claim auditor must reason about the tools
/// invoked on the turn that produced a claim, not the run aggregate. A no-tool fabrication
/// turn must be flagged even when an earlier turn in the same run used a backing tool.
/// </summary>
/// <remarks>
/// Reproduces the real incident shape (conversation c_d7790a7a...): turn A genuinely runs
/// a backing tool (<c>shell</c>), turn B narrates an artifact ("filed issue #N") with zero
/// tool calls. Under the old run-scoped auditing, turn A's <c>shell</c> "backed" turn B's
/// claim for the whole run and nothing was flagged. With per-turn auditing, turn B is
/// flagged because no backing tool ran on that turn.
/// </remarks>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerClaimAuditPerTurnTests
{
    /// <summary>
    /// A minimal backing tool named <c>shell</c> so a genuine tool turn produces a
    /// <see cref="ToolResultAgentMessage"/> whose <c>ToolName</c> lands in the auditor's
    /// backing-tool sets (matching how real GitHub work runs through <c>shell</c>).
    /// </summary>
    private sealed class ShellEchoTool : IAgentTool
    {
        private static readonly JsonElement Schema = JsonDocument.Parse(
            """{ "type": "object", "properties": { "command": { "type": "string" } } }""").RootElement.Clone();

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
            => Task.FromResult(new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]));
    }

    /// <summary>
    /// Registers a scripted provider whose responses are returned in sequence on each
    /// successive LLM call (turn 1 = first script entry, turn 2 = second, ...). The last
    /// entry is reused for any further calls.
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

    private static AgentLoopConfig ConfigWithAuditAndTool(string apiId)
    {
        // Provide the shell tool so the first turn's tool call actually executes.
        var config = TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(apiId))
            with { ClaimAudit = ClaimAuditOptions.CreateDefault() };
        return config;
    }

    private static AgentContext ContextWithShellTool()
        => new(null, [], [new ShellEchoTool()]);

    /// <summary>
    /// The core #1661 reproduction: turn A runs <c>shell</c>, turn B (same run, no new
    /// user message) narrates "filed issue #N" with no tools. The auditor must flag turn B
    /// despite turn A's backing tool.
    /// </summary>
    [Fact]
    public async Task PerTurn_FabricationTurnAfterToolTurn_IsFlagged()
    {
        const string api = "claim-audit-perturn-core";
        using var _ = RegisterScriptedProvider(
            api,
            // Turn A: a real backing tool runs.
            TestStreamFactory.CreateToolCallResponse(("call-1", "shell", new Dictionary<string, object?> { ["command"] = "gh issue list" })),
            // Turn B: pure narration, no tool calls -> the fabrication.
            TestStreamFactory.CreateTextResponse("Good news, everyone! I filed issue #1659 to track the desktop work."));
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("look into the desktop app")],
            ContextWithShellTool(),
            ConfigWithAuditAndTool(api),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        var auditEvent = events.OfType<ClaimAuditEvent>().ShouldHaveSingleItem();
        auditEvent.Result.HasUnbackedClaims.ShouldBeTrue();
        auditEvent.Result.UnbackedClaims.ShouldContain(c => c.Category == ClaimCategory.IssueFiled);
        // The flagged message is turn B's fabrication, not turn A's tool call.
        auditEvent.FinalMessage.Content.ShouldContain("filed issue #1659");
    }

    /// <summary>
    /// When the claim is made on a turn that itself runs a backing tool (the assistant text
    /// and the tool call are in the same assistant message), it is backed and not flagged.
    /// </summary>
    [Fact]
    public async Task PerTurn_ClaimInSameAssistantMessageAsToolCall_IsNotFlagged()
    {
        const string api = "claim-audit-perturn-sameturn";
        using var _ = RegisterScriptedProvider(
            api,
            // Turn A: assistant message carries BOTH narration text and a shell tool call.
            CreateTextPlusToolCallResponse(
                "I am filing issue #1659 now.",
                ("call-1", "shell", new Dictionary<string, object?> { ["command"] = "gh issue create" })),
            // Turn B: benign wrap-up, no claim.
            TestStreamFactory.CreateTextResponse("Done. The issue is tracked."));
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("file it")],
            ContextWithShellTool(),
            ConfigWithAuditAndTool(api),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        // Turn A's claim is backed by its own shell call; turn B has no claim -> no event.
        events.OfType<ClaimAuditEvent>().ShouldBeEmpty();
    }

    /// <summary>
    /// A multi-turn run where every turn is benign produces no audit event (no regression
    /// of the conservative posture across turns).
    /// </summary>
    [Fact]
    public async Task PerTurn_AllBenignTurns_EmitsNoEvent()
    {
        const string api = "claim-audit-perturn-benign";
        using var _ = RegisterScriptedProvider(
            api,
            TestStreamFactory.CreateToolCallResponse(("call-1", "shell", new Dictionary<string, object?> { ["command"] = "ls" })),
            TestStreamFactory.CreateTextResponse("I reviewed the directory listing. Everything looks correct; no action needed."));
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("check the dir")],
            ContextWithShellTool(),
            ConfigWithAuditAndTool(api),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        events.OfType<ClaimAuditEvent>().ShouldBeEmpty();
    }

    /// <summary>
    /// The single-turn fabrication case (no prior tool turn) must still be flagged -- the
    /// per-turn change must not regress the original run-final behaviour.
    /// </summary>
    [Fact]
    public async Task PerTurn_SingleTurnFabrication_StillFlagged()
    {
        const string api = "claim-audit-perturn-single";
        using var _ = RegisterScriptedProvider(
            api,
            TestStreamFactory.CreateTextResponse("I filed issue #1234 to track the regression."));
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("do the thing")],
            TestHelpers.CreateEmptyContext(),
            ConfigWithAuditAndTool(api),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        var auditEvent = events.OfType<ClaimAuditEvent>().ShouldHaveSingleItem();
        auditEvent.Result.UnbackedClaims.ShouldContain(c => c.Category == ClaimCategory.IssueFiled);
    }

    /// <summary>
    /// Builds an assistant stream whose message carries both narration text and one or more
    /// tool calls (mirrors a real model turn that narrates and acts in the same message).
    /// </summary>
    private static LlmStream CreateTextPlusToolCallResponse(
        string text,
        params (string id, string name, Dictionary<string, object?> args)[] toolCalls)
    {
        var stream = new LlmStream();
        var content = new List<ContentBlock> { new TextContent(text) };
        content.AddRange(toolCalls.Select(call => (ContentBlock)new ToolCallContent(call.id, call.name, call.args)));
        var message = new AssistantMessage(
            Content: content,
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: new Usage { Input = 10, Output = 5, TotalTokens = 15 },
            StopReason: StopReason.ToolUse,
            ErrorMessage: null,
            ResponseId: "response-1",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new StartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new TextDeltaEvent(0, text, message));
        stream.Push(new TextEndEvent(0, text, message));
        for (var i = 0; i < toolCalls.Length; i++)
        {
            var toolCall = (ToolCallContent)content[i + 1];
            stream.Push(new ToolCallStartEvent(i + 1, message));
            stream.Push(new ToolCallDeltaEvent(i + 1, "{}", message));
            stream.Push(new ToolCallEndEvent(i + 1, toolCall, message));
        }

        stream.Push(new DoneEvent(StopReason.ToolUse, message));
        stream.End(message);
        return stream;
    }
}
