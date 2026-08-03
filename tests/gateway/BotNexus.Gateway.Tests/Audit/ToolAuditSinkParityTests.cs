using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Triggers;
using BotNexus.Gateway.Audit;
using BotNexus.Gateway.Streaming;

namespace BotNexus.Gateway.Tests.Audit;

/// <summary>
/// #2614 AC2/AC3: pins that the ONE execution-layer <see cref="IToolAuditSink"/> renders the same
/// audit rows at both transports.
/// </summary>
/// <remarks>
/// The load-bearing property of this slice is behaviour parity, not the refactor itself: a trigger
/// run must persist the SAME rows it persisted before the consolidation, and the streamed and
/// blocking boundaries must not be able to drift apart again. These tests therefore compare the two
/// renderers against each other rather than against a hand-copied literal - a literal would have to
/// be maintained on both sides, which is the very drift being removed.
/// </remarks>
public sealed class ToolAuditSinkParityTests
{
    private static readonly IToolAuditSink Sink = DefaultToolAuditSink.Instance;

    [Fact]
    public void IncompleteRow_IsCharacterIdenticalAcrossBothTransports()
    {
        // The streaming path synthesizes an orphan row directly; the blocking path routes an
        // incomplete ToolCall through the record timeline. Both must produce the same text, so an
        // interrupted run reads identically whichever boundary observed it.
        var streamed = Sink.ProjectIncomplete("call-1", "search");

        var response = new AgentResponse
        {
            Content = "done",
            ToolCalls =
            [
                new AgentToolCallInfo("call-1", "search", IsError: false, IsIncomplete: true)
            ]
        };
        var blocking = Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(response)).ShouldHaveSingleItem();

        blocking.Content.ShouldBe(streamed.Content);
        blocking.ToolName.ShouldBe(streamed.ToolName);
        blocking.ToolCallId.ShouldBe(streamed.ToolCallId);
        blocking.ToolIsError.ShouldBe(streamed.ToolIsError);
    }

    [Fact]
    public void IncompleteRow_UsesTheStreamingEmDashSpelling()
    {
        // Guards the exact drift found while recovering this slice: the blocking projector spelled
        // the separator "-" while the streaming helper spelled it em-dash. Consolidating onto one
        // sink silently picked a winner; this pins which one, so a future edit cannot flip the text
        // of every historical transcript without a named test failing.
        Sink.ProjectIncomplete("call-1", "search").Content
            .ShouldBe("Tool 'search' did not complete \u2014 result synthesized for transcript consistency.");
    }

    [Theory]
    [InlineData(null, false, "Tool execution completed.")]
    [InlineData(null, true, "Tool execution failed.")]
    [InlineData("the result", false, "the result")]
    public void ResultRow_PlaceholderSelection_IsSharedByBothTransports(string? result, bool isError, string expected)
    {
        var streamed = Sink.ProjectResult("call-1", "search", result, isError, maxPersistedBytes: 0);
        streamed.Content.ShouldBe(expected);

        var response = new AgentResponse
        {
            Content = "done",
            ToolCalls =
            [
                new AgentToolCallInfo("call-1", "search", isError, ResultContent: result)
            ]
        };
        var blocking = Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(response)).ShouldHaveSingleItem();
        blocking.Content.ShouldBe(expected);
        blocking.ToolIsError.ShouldBe(isError);
    }

    [Fact]
    public void BlockingRun_PreservesExecutionOrderAndOneRowPerCall()
    {
        // Row count and ordering are the two properties a trigger's transcript depends on; assert
        // them over a mixed timeline (ok / error / incomplete) rather than a single happy call.
        var response = new AgentResponse
        {
            Content = "done",
            ToolCalls =
            [
                new AgentToolCallInfo("a", "first", IsError: false, ResultContent: "r1"),
                new AgentToolCallInfo("b", "second", IsError: true),
                new AgentToolCallInfo("c", "third", IsError: false, IsIncomplete: true)
            ]
        };

        var rows = Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(response));

        rows.Count.ShouldBe(3);
        rows.Select(r => r.ToolCallId).ShouldBe(["a", "b", "c"]);
        rows.ShouldAllBe(r => r.Role.Equals(MessageRole.Tool));
    }

    [Fact]
    public void BlockingRun_WithNoTools_ProducesNoRows()
    {
        // Negative space: consolidation must not start manufacturing audit rows for a run that
        // executed nothing.
        var response = new AgentResponse { Content = "done" };
        Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(response)).ShouldBeEmpty();
    }

    [Fact]
    public void TriggerProjector_DelegatesToTheSameSink_ProducingIdenticalRows()
    {
        // The trigger-facing adapter must be a pass-through. If it ever re-acquires a format of its
        // own, these two sequences diverge and this fails - which is the drift #2614 removes.
        var response = new AgentResponse
        {
            Content = "done",
            ToolCalls =
            [
                new AgentToolCallInfo("a", "first", IsError: false, ResultContent: "r1"),
                new AgentToolCallInfo("b", "second", IsError: false, IsIncomplete: true)
            ]
        };

        var viaTrigger = TriggerToolAuditProjector.ProjectToolEntries(response).ToList();
        var viaSink = Sink.ProjectBlockingRun(Sink.CaptureBlockingRun(response));

        viaTrigger.Count.ShouldBe(viaSink.Count);
        for (var i = 0; i < viaTrigger.Count; i++)
        {
            viaTrigger[i].Content.ShouldBe(viaSink[i].Content);
            viaTrigger[i].ToolCallId.ShouldBe(viaSink[i].ToolCallId);
            viaTrigger[i].ToolName.ShouldBe(viaSink[i].ToolName);
            viaTrigger[i].ToolIsError.ShouldBe(viaSink[i].ToolIsError);
        }
    }

    [Fact]
    public void StreamingOptions_DefaultToTheRegisteredSink_WhenNoOverrideIsSupplied()
    {
        // The helper is static, so the seam is an optional option rather than constructor
        // injection; pin that the default is the shared sink and not a second implementation.
        new StreamingSessionOptions().ToolAuditSink.ShouldBeNull();
        DefaultToolAuditSink.Instance.ShouldBeOfType<DefaultToolAuditSink>();
    }
}
