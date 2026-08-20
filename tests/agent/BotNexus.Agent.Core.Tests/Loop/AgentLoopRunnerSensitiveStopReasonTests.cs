using System.Text.Json;
using BotNexus.Agent.Core.Configuration;
using BotNexus.Agent.Core.Diagnostics;
using BotNexus.Agent.Core.Loop;
using BotNexus.Agent.Core.Tests.TestUtils;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Core.Tests.Loop;

using AgentUserMessage = BotNexus.Agent.Core.Types.UserMessage;

/// <summary>
/// Pins the agent loop's behaviour for a content-filtered turn (<see cref="StopReason.Sensitive"/>),
/// which #3296 made reachable on the Chat Completions path for the first time.
/// </summary>
/// <remarks>
/// Before #3296 the Completions engine mapped <c>finish_reason: "content_filter"</c> to
/// <see cref="StopReason.Error"/>, so every filtered Completions turn took the loop's
/// error-termination path. Remapping it to <c>Sensitive</c> - which the Responses parser already
/// used for the same upstream condition - moves those turns onto a DIFFERENT loop path, because
/// <c>Sensitive</c> is not in the <c>is StopReason.Error or StopReason.Aborted</c> early-return.
/// <para>
/// The issue's fourth acceptance criterion exists precisely so that path change is asserted rather
/// than inherited silently. These tests are that assertion: they state what a <c>Sensitive</c> turn
/// does in the loop, so a later change to either the mapping or the termination predicate has to
/// come here and argue with a named test instead of quietly altering run behaviour.
/// </para>
/// </remarks>
[Collection(ApiProviderRegistryCollection.Name)]
public class AgentLoopRunnerSensitiveStopReasonTests
{
    /// <summary>
    /// A tool that records dispatches, so "a filtered turn must not execute a half-formed tool
    /// call" can be asserted on execution count rather than on the absence of a result message.
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

    private static IDisposable RegisterStreamProvider(string apiId, Func<LlmStream> streamFactory)
        => TestHelpers.RegisterProvider(new TestApiProvider(apiId, simpleStreamFactory: (_, _, _) => streamFactory()));

    /// <summary>
    /// Builds a turn that mirrors what the Completions engine now produces for
    /// <c>finish_reason: "content_filter"</c>: partial text, the <c>Sensitive</c> terminal, and the
    /// preserved human-readable message.
    /// </summary>
    private static LlmStream CreateSensitiveResponse(
        string text = "Here is how you ",
        IReadOnlyList<ContentBlock>? content = null)
    {
        var stream = new LlmStream();
        var message = new AssistantMessage(
            Content: content ?? [new TextContent(text)],
            Api: "test-api",
            Provider: "test-provider",
            ModelId: "test-model",
            Usage: new Usage { Input = 10, Output = 5, TotalTokens = 15 },
            StopReason: StopReason.Sensitive,
            ErrorMessage: "Content filtered by provider",
            ResponseId: "response-1",
            Timestamp: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        stream.Push(new StartEvent(message));
        stream.Push(new TextStartEvent(0, message));
        stream.Push(new TextDeltaEvent(0, text, message));
        stream.Push(new TextEndEvent(0, text, message));
        stream.Push(new DoneEvent(StopReason.Sensitive, message));
        stream.End(message);
        return stream;
    }

    /// <summary>
    /// The core path assertion: a filtered turn settles the run through the ORDINARY completion
    /// path, so it emits a TurnEndEvent and an AgentEndEvent and the run ends - it neither
    /// short-circuits through the error branch nor spins the loop for another turn.
    /// </summary>
    [Fact]
    public async Task SensitiveTurn_CompletesTheRunThroughTheOrdinaryTurnEndPath()
    {
        const string api = "sensitive-ordinary-path";
        using var _ = RegisterStreamProvider(api, () => CreateSensitiveResponse());
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("do the thing")],
            TestHelpers.CreateEmptyContext(),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        var turnEnd = events.OfType<TurnEndEvent>().ShouldHaveSingleItem();
        turnEnd.Message.FinishReason.ShouldBe(StopReason.Sensitive);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem();

        // Exactly one turn: a Sensitive terminal must not be treated as "keep going".
        events.OfType<TurnStartEvent>().Count().ShouldBeLessThanOrEqualTo(1);
    }

    /// <summary>
    /// The filtered turn is persisted as a Sensitive assistant message carrying its partial text
    /// and the provider's explanation, NOT as an error placeholder. This is the user-visible half
    /// of #3296: a policy decision must be recorded as one.
    /// </summary>
    [Fact]
    public async Task SensitiveTurn_IsPersistedAsSensitiveWithItsMessage()
    {
        const string api = "sensitive-persisted";
        using var _ = RegisterStreamProvider(api, () => CreateSensitiveResponse());

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("do the thing")],
            TestHelpers.CreateEmptyContext(),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        var assistant = result.OfType<AssistantAgentMessage>().ShouldHaveSingleItem();
        assistant.FinishReason.ShouldBe(StopReason.Sensitive);
        assistant.FinishReason.ShouldNotBe(
            StopReason.Error,
            "a content-filtered turn must not be recorded as a provider error (#3296) - it would " +
            "misattribute a safety decision as an infrastructure failure in persisted history");
        assistant.Content.ShouldContain("Here is how you ");
        assistant.ErrorMessage.ShouldBe("Content filtered by provider");
    }

    /// <summary>
    /// A filtered turn that nonetheless surfaced a parsed tool call must not dispatch it - the same
    /// #1666 property already asserted for <see cref="StopReason.Length"/>. The filter truncated the
    /// turn, so the call is half-formed by construction.
    /// </summary>
    [Fact]
    public async Task SensitiveTurn_DoesNotDispatchASurfacedToolCall()
    {
        const string api = "sensitive-no-dispatch";
        var tool = new RecordingTool();
        var toolCall = new ToolCallContent("call-1", "shell", new Dictionary<string, object?> { ["command"] = "rm -rf" });
        using var _ = RegisterStreamProvider(
            api,
            () => CreateSensitiveResponse(content: [new TextContent("Here is how you "), toolCall]));

        var result = await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("do the thing")],
            new AgentContext(null, [], [tool]),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            _ => Task.CompletedTask,
            CancellationToken.None);

        tool.ExecuteCount.ShouldBe(0);
        result.OfType<ToolResultAgentMessage>().ShouldBeEmpty();
    }

    /// <summary>
    /// The claim auditor must skip a filtered turn. Its surviving text is a fragment of an intent
    /// the provider prevented the model from finishing, not a claim the model is asserting, so
    /// auditing it would manufacture unbacked-claim noise. Before #3296 this exclusion came free
    /// from the Error branch; the loop now states it explicitly and this test pins it.
    /// </summary>
    [Fact]
    public async Task SensitiveTurn_IsNotClaimAudited()
    {
        const string api = "sensitive-not-audited";
        using var _ = RegisterStreamProvider(
            api,
            () => CreateSensitiveResponse("Good news, everyone! I filed issue #1234 to track "));
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("do the thing")],
            TestHelpers.CreateEmptyContext(),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
            {
                ClaimAudit = ClaimAuditOptions.CreateDefault()
            },
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        events.OfType<ClaimAuditEvent>().ShouldBeEmpty(
            "a content-filtered turn's surviving text is a truncated fragment, not a claim (#3296)");
    }

    /// <summary>
    /// Non-vacuity guard for the test above: the SAME fabricated text on an ordinary Stop turn DOES
    /// produce a ClaimAuditEvent. Without this, the exclusion could be satisfied by an auditor that
    /// never fires at all.
    /// </summary>
    [Fact]
    public async Task OrdinaryTurn_WithTheSameText_IsStillClaimAudited()
    {
        const string api = "sensitive-audit-control";
        using var _ = RegisterStreamProvider(
            api,
            () => TestStreamFactory.CreateTextResponse(
                "Good news, everyone! I filed issue #1234 to track ",
                StopReason.Stop));
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("do the thing")],
            TestHelpers.CreateEmptyContext(),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)) with
            {
                ClaimAudit = ClaimAuditOptions.CreateDefault()
            },
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        events.OfType<ClaimAuditEvent>().ShouldNotBeEmpty();
    }

    /// <summary>
    /// The contrast case, stated as a test so the two paths cannot silently converge: an Error turn
    /// still takes the early-return branch. Both paths end the run, but only Sensitive gets there
    /// through the ordinary TurnEnd with its tool-result list.
    /// </summary>
    [Fact]
    public async Task ErrorTurn_StillTakesTheEarlyReturnPath()
    {
        const string api = "sensitive-error-contrast";
        using var _ = RegisterStreamProvider(api, () => TestStreamFactory.CreateErrorResponse("boom"));
        var events = new List<AgentEvent>();

        await AgentLoopRunner.RunAsync(
            [new AgentUserMessage("do the thing")],
            TestHelpers.CreateEmptyContext(),
            TestHelpers.CreateTestConfig(model: TestHelpers.CreateTestModel(api)),
            evt => { events.Add(evt); return Task.CompletedTask; },
            CancellationToken.None);

        var turnEnd = events.OfType<TurnEndEvent>().ShouldHaveSingleItem();
        turnEnd.Message.FinishReason.ShouldBe(StopReason.Error);
        events.OfType<AgentEndEvent>().ShouldHaveSingleItem();
    }
}
