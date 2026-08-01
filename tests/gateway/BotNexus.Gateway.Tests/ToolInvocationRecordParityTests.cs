using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Isolation;
using BotNexus.Gateway.Streaming;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// #2613: the complete tool timeline must survive the blocking <c>PromptAsync</c> boundary with the
/// same fidelity the streaming path already persists. These tests pin the shared
/// <see cref="ToolInvocationRecord"/> shape (AC1), its population from the blocking path in execution
/// order including parallel calls (AC2), parity between <c>StreamAsync</c> and <c>PromptAsync</c>
/// (AC3), and the single truncation/redaction policy applied at the record boundary (AC4).
/// </summary>
public sealed class ToolInvocationRecordParityTests
{
    // ── AC1: record shape ────────────────────────────────────────────────────

    /// <summary>
    /// AC1: one record type carries call id, tool name, redacted arguments, result/error, start and
    /// completion timestamps, ordering index, and parent/child correlation.
    /// </summary>
    [Fact]
    public void ToolInvocationRecord_CarriesTheCompleteTimelineShape()
    {
        var started = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var completed = started.AddSeconds(2);

        var record = ToolInvocationRecordPolicy.Default.Create(
            orderIndex: 3,
            toolCallId: "call-1",
            toolName: "read",
            rawArguments: "{\"path\":\"a.txt\"}",
            rawResultContent: "file body",
            isError: false,
            isIncomplete: false,
            startedAt: started,
            completedAt: completed,
            parentToolCallId: "parent-1");

        record.OrderIndex.ShouldBe(3);
        record.ToolCallId.ShouldBe("call-1");
        record.ToolName.ShouldBe("read");
        record.Arguments.ShouldBe("{\"path\":\"a.txt\"}");
        record.ResultContent.ShouldBe("file body");
        record.IsError.ShouldBeFalse();
        record.IsIncomplete.ShouldBeFalse();
        record.StartedAt.ShouldBe(started);
        record.CompletedAt.ShouldBe(completed);
        record.ParentToolCallId.ShouldBe("parent-1");
    }

    // ── AC4: one policy, applied at the record boundary ──────────────────────

    /// <summary>
    /// AC4: secret-shaped material in the arguments and the result is redacted by the record
    /// boundary itself, so no caller can construct an unredacted record.
    /// </summary>
    [Fact]
    public void Create_RedactsSecretsInArgumentsAndResult()
    {
        var record = ToolInvocationRecordPolicy.Default.Create(
            orderIndex: 0,
            toolCallId: "call-secret",
            toolName: "web_fetch",
            rawArguments: "{\"token\":\"ghp_0123456789abcdefghijklmnopqrstuvwxyzAB\"}",
            rawResultContent: "authorized with ghp_0123456789abcdefghijklmnopqrstuvwxyzAB",
            isError: false,
            isIncomplete: false,
            startedAt: DateTimeOffset.UnixEpoch,
            completedAt: DateTimeOffset.UnixEpoch);

        record.Arguments.ShouldNotBeNull();
        record.Arguments!.ShouldNotContain("ghp_0123456789abcdefghijklmnopqrstuvwxyzAB");
        record.ResultContent.ShouldNotBeNull();
        record.ResultContent!.ShouldNotContain("ghp_0123456789abcdefghijklmnopqrstuvwxyzAB");
    }

    /// <summary>
    /// AC4: the same byte budget that caps a persisted tool result caps the record's arguments and
    /// result, using the existing rune-safe truncation helper rather than a second policy.
    /// </summary>
    [Fact]
    public void Create_TruncatesOversizedArgumentsAndResult()
    {
        var policy = new ToolInvocationRecordPolicy(maxArgumentBytes: 64, maxResultBytes: 64);
        var huge = new string('x', 4096);

        var record = policy.Create(
            orderIndex: 0,
            toolCallId: "call-big",
            toolName: "read",
            rawArguments: huge,
            rawResultContent: huge,
            isError: false,
            isIncomplete: false,
            startedAt: DateTimeOffset.UnixEpoch,
            completedAt: DateTimeOffset.UnixEpoch);

        record.Arguments.ShouldNotBeNull();
        record.Arguments!.Length.ShouldBeLessThan(huge.Length);
        record.Arguments.ShouldContain("[truncated");
        record.ResultContent.ShouldNotBeNull();
        record.ResultContent!.Length.ShouldBeLessThan(huge.Length);
        record.ResultContent.ShouldContain("[truncated");
    }

    // ── AC2: blocking path populates records in execution order ──────────────

    /// <summary>
    /// AC2: <c>PromptAsync</c>'s projection emits one record per executed call in execution order,
    /// including two calls issued in parallel on a single assistant message, with contiguous
    /// ordering indices assigned where the execution order is actually known.
    /// </summary>
    [Fact]
    public void BuildToolInvocations_ParallelCalls_AreOrderedAndCarryArguments()
    {
        var records = InProcessAgentHandle.BuildToolInvocations(ScriptedTimeline(), pendingToolCallIds: null);

        records.Count.ShouldBe(3);
        records.Select(r => r.OrderIndex).ShouldBe([0, 1, 2]);
        records.Select(r => r.ToolCallId).ShouldBe(["call-1", "call-2", "call-3"]);

        records[0].ToolName.ShouldBe("read");
        records[0].Arguments.ShouldNotBeNull();
        records[0].Arguments!.ShouldContain("a.txt");
        records[0].ResultContent.ShouldBe("file body");
        records[0].IsError.ShouldBeFalse();

        records[1].Arguments.ShouldNotBeNull();
        records[1].Arguments!.ShouldContain("b.txt");
        records[1].ResultContent.ShouldBe("ok");

        records[2].Arguments.ShouldNotBeNull();
        records[2].Arguments!.ShouldContain("https://example.test");
        records[2].IsError.ShouldBeTrue();
        records[2].ResultContent.ShouldBe("boom");
    }

    /// <summary>
    /// AC2: a call still pending when the run was interrupted is carried as an incomplete record
    /// with its arguments intact and no completion timestamp.
    /// </summary>
    [Fact]
    public void BuildToolInvocations_PendingCall_IsIncompleteWithArgumentsPreserved()
    {
        var messages = new List<AgentMessage>
        {
            new AssistantAgentMessage(
                "calling",
                ToolCalls: [new ToolCallContent("call-inflight", "web_fetch", new Dictionary<string, object?> { ["url"] = "x" })])
        };

        var records = InProcessAgentHandle.BuildToolInvocations(
            messages,
            new HashSet<string>(StringComparer.Ordinal) { "call-inflight" });

        records.ShouldHaveSingleItem();
        records[0].IsIncomplete.ShouldBeTrue();
        records[0].IsError.ShouldBeTrue();
        records[0].CompletedAt.ShouldBeNull();
        records[0].Arguments.ShouldNotBeNull();
        records[0].Arguments!.ShouldContain("x");
    }

    // ── AC3: StreamAsync / PromptAsync parity ────────────────────────────────

    /// <summary>
    /// AC3: for one scripted tool sequence, the streaming path and the blocking path produce
    /// equivalent records - same count, same ordering, same ids, names, arguments, results and error
    /// state. Timestamps are excluded from the comparison because the two paths observe different
    /// clocks; their presence is asserted separately.
    /// </summary>
    [Fact]
    public async Task StreamAsync_And_PromptAsync_ProduceEquivalentRecords()
    {
        var fromPrompt = InProcessAgentHandle.BuildToolInvocations(ScriptedTimeline(), pendingToolCallIds: null);

        var session = new GatewaySession
        {
            SessionId = BotNexus.Domain.Primitives.SessionId.From("session-2613"),
            AgentId = BotNexus.Domain.Primitives.AgentId.From("agent-2613")
        };
        var store = new Mock<ISessionStore>();
        var result = await StreamingSessionHelper.ProcessAndSaveAsync(
            ToAsyncEnumerable(ScriptedStreamEvents()),
            session,
            store.Object);
        var fromStream = result.ToolInvocations;

        fromStream.Count.ShouldBe(fromPrompt.Count);
        for (var i = 0; i < fromPrompt.Count; i++)
        {
            fromStream[i].OrderIndex.ShouldBe(fromPrompt[i].OrderIndex);
            fromStream[i].ToolCallId.ShouldBe(fromPrompt[i].ToolCallId);
            fromStream[i].ToolName.ShouldBe(fromPrompt[i].ToolName);
            fromStream[i].Arguments.ShouldBe(fromPrompt[i].Arguments);
            fromStream[i].ResultContent.ShouldBe(fromPrompt[i].ResultContent);
            fromStream[i].IsError.ShouldBe(fromPrompt[i].IsError);
            fromStream[i].IsIncomplete.ShouldBe(fromPrompt[i].IsIncomplete);
        }

        fromStream.ShouldAllBe(r => r.CompletedAt != null);
        fromPrompt.ShouldAllBe(r => r.CompletedAt != null);
    }

    // ── Scripted fixtures shared by both paths ───────────────────────────────

    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The blocking-path view of the scripted sequence: two parallel calls, then a failing call.</summary>
    private static List<AgentMessage> ScriptedTimeline() =>
    [
        new BotNexus.Agent.Core.Types.UserMessage("go"),
        new AssistantAgentMessage(
            "calling",
            ToolCalls:
            [
                new ToolCallContent("call-1", "read", new Dictionary<string, object?> { ["path"] = "a.txt" }),
                new ToolCallContent("call-2", "write", new Dictionary<string, object?> { ["path"] = "b.txt" })
            ],
            Timestamp: Base),
        new ToolResultAgentMessage("call-1", "read",
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "file body")]),
            IsError: false, Timestamp: Base.AddSeconds(1)),
        new ToolResultAgentMessage("call-2", "write",
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "ok")]),
            IsError: false, Timestamp: Base.AddSeconds(1)),
        new AssistantAgentMessage(
            "calling again",
            ToolCalls: [new ToolCallContent("call-3", "web_fetch", new Dictionary<string, object?> { ["url"] = "https://example.test" })],
            Timestamp: Base.AddSeconds(2)),
        new ToolResultAgentMessage("call-3", "web_fetch",
            new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "boom")]),
            IsError: true, Timestamp: Base.AddSeconds(3)),
        new AssistantAgentMessage("done", Timestamp: Base.AddSeconds(4))
    ];

    /// <summary>The streaming-path view of the very same scripted sequence.</summary>
    private static AgentStreamEvent[] ScriptedStreamEvents() =>
    [
        new() { Type = AgentStreamEventType.MessageStart, MessageId = "m1" },
        new() { Type = AgentStreamEventType.ToolStart, ToolCallId = "call-1", ToolName = "read", ToolArgs = new Dictionary<string, object?> { ["path"] = "a.txt" }, Timestamp = Base },
        new() { Type = AgentStreamEventType.ToolStart, ToolCallId = "call-2", ToolName = "write", ToolArgs = new Dictionary<string, object?> { ["path"] = "b.txt" }, Timestamp = Base },
        new() { Type = AgentStreamEventType.ToolEnd, ToolCallId = "call-1", ToolName = "read", ToolResult = "file body", ToolIsError = false, Timestamp = Base.AddSeconds(1) },
        new() { Type = AgentStreamEventType.ToolEnd, ToolCallId = "call-2", ToolName = "write", ToolResult = "ok", ToolIsError = false, Timestamp = Base.AddSeconds(1) },
        new() { Type = AgentStreamEventType.ToolStart, ToolCallId = "call-3", ToolName = "web_fetch", ToolArgs = new Dictionary<string, object?> { ["url"] = "https://example.test" }, Timestamp = Base.AddSeconds(2) },
        new() { Type = AgentStreamEventType.ToolEnd, ToolCallId = "call-3", ToolName = "web_fetch", ToolResult = "boom", ToolIsError = true, Timestamp = Base.AddSeconds(3) },
        new() { Type = AgentStreamEventType.ContentDelta, ContentDelta = "done", MessageId = "m1" },
        new() { Type = AgentStreamEventType.MessageEnd, MessageId = "m1" }
    ];

    private static async IAsyncEnumerable<AgentStreamEvent> ToAsyncEnumerable(AgentStreamEvent[] events)
    {
        foreach (var evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }
}
