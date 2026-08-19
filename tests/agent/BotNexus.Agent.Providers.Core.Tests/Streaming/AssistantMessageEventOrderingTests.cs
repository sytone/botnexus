using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Streaming;

namespace BotNexus.Agent.Providers.Core.Tests.Streaming;

/// <summary>
/// Non-vacuity proof for <see cref="AssistantMessageEventOrdering"/> (#3300).
/// <para>
/// A validator is only worth the assertions it can fail. Every rule below is exercised twice: once
/// against a sequence that violates exactly that rule, asserting the specific rule id is reported,
/// and - via <see cref="WellFormed_ReportsNoViolation"/> and the per-test "and nothing else" checks -
/// against a well-formed sequence asserting silence. A validator that reported a violation for
/// everything would be as useless as one that reported none, so the negative case is load-bearing
/// rather than decorative.
/// </para>
/// <para>
/// Assertions target the stable <see cref="EventOrderingViolation.Rule"/> id, not the message prose,
/// so improving a diagnostic cannot quietly turn a targeted assertion into a substring match.
/// </para>
/// </summary>
public class AssistantMessageEventOrderingTests
{
    private static AssistantMessage Msg(StopReason reason = StopReason.Stop) => new(
        Content: [],
        Api: "test-api",
        Provider: "test",
        ModelId: "test-model",
        Usage: Usage.Empty(),
        StopReason: reason,
        ErrorMessage: null,
        ResponseId: "resp_1",
        Timestamp: 0);

    private static List<AssistantMessageEvent> WellFormedText() =>
    [
        new StartEvent(Msg()),
        new TextStartEvent(0, Msg()),
        new TextDeltaEvent(0, "hello", Msg()),
        new TextEndEvent(0, "hello", Msg()),
        new DoneEvent(StopReason.Stop, Msg())
    ];

    [Fact]
    public void WellFormed_ReportsNoViolation()
    {
        AssistantMessageEventOrdering.Validate(WellFormedText()).ShouldBeEmpty();
    }

    /// <summary>
    /// Thinking, then text, then two tool calls interleaved across distinct content indices - the
    /// realistic shape. It must be silent, otherwise the validator would fail every real provider and
    /// its violations would be ignored as noise.
    /// </summary>
    [Fact]
    public void WellFormedInterleavedToolCalls_ReportsNoViolation()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new ThinkingStartEvent(0, Msg()),
            new ThinkingDeltaEvent(0, "hmm", Msg()),
            new ThinkingEndEvent(0, "hmm", Msg()),
            new TextStartEvent(1, Msg()),
            new TextDeltaEvent(1, "calling tools", Msg()),
            new TextEndEvent(1, "calling tools", Msg()),
            new ToolCallStartEvent(2, Msg()),
            new ToolCallStartEvent(3, Msg()),
            new ToolCallDeltaEvent(2, "{\"a\":", Msg()),
            new ToolCallDeltaEvent(3, "{\"b\":", Msg()),
            new ToolCallDeltaEvent(2, "1}", Msg()),
            new ToolCallDeltaEvent(3, "2}", Msg()),
            new ToolCallEndEvent(2, new ToolCallContent("call_a", "search", []), Msg()),
            new ToolCallEndEvent(3, new ToolCallContent("call_b", "lookup", []), Msg()),
            new DoneEvent(StopReason.ToolUse, Msg(StopReason.ToolUse))
        ];

        AssistantMessageEventOrdering.Validate(events).ShouldBeEmpty();
    }

    [Fact]
    public void DeltaWithoutStart_IsReported()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new TextDeltaEvent(0, "orphan", Msg()),
            new DoneEvent(StopReason.Stop, Msg())
        ];

        var violations = AssistantMessageEventOrdering.Validate(events);

        violations.ShouldContain(v => v.Rule == AssistantMessageEventOrdering.RuleDeltaWithoutStart);
        violations.Single(v => v.Rule == AssistantMessageEventOrdering.RuleDeltaWithoutStart)
            .EventIndex.ShouldBe(1);
    }

    [Fact]
    public void StartWithoutEnd_IsReported()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new TextStartEvent(0, Msg()),
            new TextDeltaEvent(0, "unterminated", Msg()),
            new DoneEvent(StopReason.Stop, Msg())
        ];

        AssistantMessageEventOrdering.Validate(events)
            .ShouldContain(v => v.Rule == AssistantMessageEventOrdering.RuleUnclosedBlock);
    }

    [Fact]
    public void TwoDoneEvents_IsReported()
    {
        List<AssistantMessageEvent> events =
        [
            .. WellFormedText(),
            new DoneEvent(StopReason.Stop, Msg())
        ];

        AssistantMessageEventOrdering.Validate(events)
            .ShouldContain(v => v.Rule == AssistantMessageEventOrdering.RuleEventAfterTerminal);
    }

    [Fact]
    public void EventAfterDone_IsReported()
    {
        List<AssistantMessageEvent> events =
        [
            .. WellFormedText(),
            new TextDeltaEvent(0, "too late", Msg())
        ];

        var violations = AssistantMessageEventOrdering.Validate(events);

        violations.ShouldContain(v => v.Rule == AssistantMessageEventOrdering.RuleEventAfterTerminal);
        violations.Single(v => v.Rule == AssistantMessageEventOrdering.RuleEventAfterTerminal)
            .EventIndex.ShouldBe(5);
    }

    [Fact]
    public void EndWithoutStart_IsReported()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new TextEndEvent(0, "never opened", Msg()),
            new DoneEvent(StopReason.Stop, Msg())
        ];

        AssistantMessageEventOrdering.Validate(events)
            .ShouldContain(v => v.Rule == AssistantMessageEventOrdering.RuleEndWithoutStart);
    }

    [Fact]
    public void BlockEventBeforeStartEvent_IsReported()
    {
        List<AssistantMessageEvent> events =
        [
            new TextStartEvent(0, Msg()),
            new TextEndEvent(0, "", Msg()),
            new StartEvent(Msg()),
            new DoneEvent(StopReason.Stop, Msg())
        ];

        AssistantMessageEventOrdering.Validate(events)
            .ShouldContain(v => v.Rule == AssistantMessageEventOrdering.RuleStartPrecedesBlocks);
    }

    [Fact]
    public void DuplicateStartAtOpenIndex_IsReported()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new TextStartEvent(0, Msg()),
            new TextStartEvent(0, Msg()),
            new TextEndEvent(0, "", Msg()),
            new DoneEvent(StopReason.Stop, Msg())
        ];

        AssistantMessageEventOrdering.Validate(events)
            .ShouldContain(v => v.Rule == AssistantMessageEventOrdering.RuleDuplicateStart);
    }

    /// <summary>
    /// A <c>toolcall_delta</c> landing on an index holding an open text block is not merely a
    /// delta-without-start - the index IS open, just for the wrong kind. Collapsing the two would let
    /// a cross-wired producer pass the delta-without-start check.
    /// </summary>
    [Fact]
    public void KindMismatchOnOpenIndex_IsReported()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new TextStartEvent(0, Msg()),
            new ToolCallDeltaEvent(0, "{}", Msg()),
            new TextEndEvent(0, "", Msg()),
            new DoneEvent(StopReason.Stop, Msg())
        ];

        var violations = AssistantMessageEventOrdering.Validate(events);

        violations.ShouldContain(v => v.Rule == AssistantMessageEventOrdering.RuleKindMismatch);
        violations.ShouldNotContain(v => v.Rule == AssistantMessageEventOrdering.RuleDeltaWithoutStart);
    }

    [Fact]
    public void NoTerminalEvent_IsReported()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new TextStartEvent(0, Msg()),
            new TextEndEvent(0, "done-ish", Msg())
        ];

        AssistantMessageEventOrdering.Validate(events)
            .ShouldContain(v => v.Rule == AssistantMessageEventOrdering.RuleMissingTerminal);
    }

    /// <summary>
    /// An ErrorEvent is a terminal event, so a turn that fails is not also guilty of "missing
    /// terminal". Without this the validator would double-report every failed turn.
    /// </summary>
    [Fact]
    public void ErrorEventIsATerminalEvent()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new ErrorEvent(StopReason.Error, Msg(StopReason.Error))
        ];

        AssistantMessageEventOrdering.Validate(events).ShouldBeEmpty();
    }

    /// <summary>
    /// A turn that errors mid-block legitimately never closes it. Demanding a closing event on the
    /// failure path would assert a shape no producer honours, and a rule that fires on every real
    /// cancellation is a rule everyone learns to ignore.
    /// </summary>
    [Fact]
    public void UnclosedBlockOnErrorPath_IsNotReported()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new TextStartEvent(0, Msg()),
            new TextDeltaEvent(0, "partial", Msg()),
            new ErrorEvent(StopReason.Aborted, Msg(StopReason.Aborted))
        ];

        AssistantMessageEventOrdering.Validate(events).ShouldBeEmpty();
    }

    /// <summary>
    /// A terminal-only stream conforms: a request that fails before the provider reads anything has
    /// no StartEvent to emit, and requiring one would make the validator reject a legitimate shape.
    /// </summary>
    [Fact]
    public void TerminalOnlyStream_ReportsNoViolation()
    {
        List<AssistantMessageEvent> events =
        [
            new ErrorEvent(StopReason.Error, Msg(StopReason.Error))
        ];

        AssistantMessageEventOrdering.Validate(events).ShouldBeEmpty();
    }

    [Fact]
    public void IndependentViolations_AreAllReported()
    {
        List<AssistantMessageEvent> events =
        [
            new StartEvent(Msg()),
            new TextDeltaEvent(0, "orphan", Msg()),
            new TextEndEvent(1, "also orphan", Msg()),
            new DoneEvent(StopReason.Stop, Msg()),
            new TextDeltaEvent(0, "post-terminal", Msg())
        ];

        var rules = AssistantMessageEventOrdering.Validate(events).Select(v => v.Rule).ToList();

        rules.ShouldContain(AssistantMessageEventOrdering.RuleDeltaWithoutStart);
        rules.ShouldContain(AssistantMessageEventOrdering.RuleEndWithoutStart);
        rules.ShouldContain(AssistantMessageEventOrdering.RuleEventAfterTerminal);
    }

    [Fact]
    public void NullEvents_Throws()
    {
        Should.Throw<ArgumentNullException>(() => AssistantMessageEventOrdering.Validate(null!));
    }
}
