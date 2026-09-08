using System.Collections.Generic;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// A single breach of the normalized event grammar, naming the rule that was broken and the
/// position at which it was broken.
/// </summary>
/// <param name="Rule">
/// Stable machine-readable rule identifier (e.g. <c>delta-without-start</c>). Tests assert on this
/// rather than on <paramref name="Message"/> so that improving a diagnostic message cannot silently
/// turn a targeted assertion into a substring match on prose.
/// </param>
/// <param name="EventIndex">
/// Zero-based position in the captured event list at which the breach was observed, or the list
/// length for a breach that is only observable once the stream has ended (an unclosed block).
/// </param>
/// <param name="Message">Human-readable explanation, for test failure output.</param>
public sealed record EventOrderingViolation(string Rule, int EventIndex, string Message);

/// <summary>
/// Producer-agnostic validator for the ordering invariants of the normalized
/// <see cref="AssistantMessageEvent"/> stream (#3300).
/// <para>
/// These invariants were previously enforced only by each producer's own control flow, and the one
/// ordering test in the shared conformance suite let every provider fixture declare its own expected
/// sequence - so a producer that emitted a self-consistently wrong sequence declared that wrong
/// sequence as its expectation and passed. This type exists so the grammar is stated once, in terms
/// no producer can supply, and checked against every producer.
/// </para>
/// <para>The rules, in the order they are checked:</para>
/// <list type="number">
/// <item><description>
/// A <see cref="StartEvent"/>, when present, is the first event, and no block event may precede it.
/// It is not required unconditionally: a turn that fails or is cancelled before the provider has
/// read anything legitimately emits only a terminal event.
/// </description></item>
/// <item><description>
/// Every <c>*_delta</c> and <c>*_end</c> at content index <i>i</i> is preceded by a matching
/// <c>*_start</c> at <i>i</i> of the same kind, and each open block is closed exactly once.
/// </description></item>
/// <item><description>Exactly one terminal event (<see cref="DoneEvent"/> or <see cref="ErrorEvent"/>).</description></item>
/// <item><description>Nothing is emitted after the terminal event.</description></item>
/// </list>
/// <para>
/// <see cref="WarningEvent"/> is deliberately outside this grammar (#3291): it is non-terminal,
/// opens and closes no block, and may appear at any position - including before the
/// <see cref="StartEvent"/>, when the first frame a producer sees is the malformed one.
/// </para>
/// <para>
/// The "nothing after the terminal event" rule matters more than it looks:
/// <see cref="LlmStream.Push"/> drops post-terminal pushes silently, so a producer that violates it
/// is invisible at runtime as well as in tests. Validating the captured list rather than the stream
/// is what makes the breach observable.
/// </para>
/// </summary>
public static class AssistantMessageEventOrdering
{
    /// <summary>Rule id: a block event was emitted before the <see cref="StartEvent"/>.</summary>
    public const string RuleStartPrecedesBlocks = "start-precedes-blocks";

    /// <summary>Rule id: more than one <see cref="StartEvent"/> was emitted.</summary>
    public const string RuleSingleStart = "single-start";

    /// <summary>Rule id: a <c>*_delta</c> arrived at an index with no open block.</summary>
    public const string RuleDeltaWithoutStart = "delta-without-start";

    /// <summary>Rule id: an <c>*_end</c> arrived at an index with no open block.</summary>
    public const string RuleEndWithoutStart = "end-without-start";

    /// <summary>Rule id: a second <c>*_start</c> arrived at an index whose block is still open.</summary>
    public const string RuleDuplicateStart = "duplicate-start";

    /// <summary>Rule id: a <c>*_delta</c>/<c>*_end</c> kind did not match the open block's kind.</summary>
    public const string RuleKindMismatch = "kind-mismatch";

    /// <summary>Rule id: a block was still open when the stream completed successfully.</summary>
    public const string RuleUnclosedBlock = "unclosed-block";

    /// <summary>Rule id: no terminal event was emitted at all.</summary>
    public const string RuleMissingTerminal = "missing-terminal";

    /// <summary>Rule id: more than one terminal event was emitted.</summary>
    public const string RuleMultipleTerminals = "multiple-terminals";

    /// <summary>Rule id: an event was emitted after the terminal event.</summary>
    public const string RuleEventAfterTerminal = "event-after-terminal";

    /// <summary>
    /// Validate a captured event list and return every violation found, in the order observed.
    /// An empty result means the sequence conforms.
    /// </summary>
    /// <remarks>
    /// Validation deliberately does not stop at the first violation: a producer with two independent
    /// grammar bugs should surface both, otherwise fixing one merely reveals the other on the next
    /// run.
    /// </remarks>
    public static IReadOnlyList<EventOrderingViolation> Validate(
        IReadOnlyList<AssistantMessageEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var violations = new List<EventOrderingViolation>();
        var openBlocks = new Dictionary<int, string>();
        var startSeen = false;
        // Whether any event other than a non-terminal WarningEvent has been observed. The start rule
        // is "nothing of substance precedes StartEvent", not "StartEvent is literally at index 0":
        // a producer can observe a malformed first frame and legitimately report it (#3291) before
        // it has parsed enough to open the message. Keying off this flag rather than the raw index
        // does not weaken the rule - a block event before StartEvent is still caught, by the
        // !startSeen check further down, which warnings never reach.
        var nonWarningSeen = false;
        var terminalIndex = -1;
        var terminalIsError = false;

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];

            if (terminalIndex >= 0)
            {
                violations.Add(new EventOrderingViolation(
                    RuleEventAfterTerminal,
                    i,
                    $"'{evt.Type}' was emitted after the terminal event at position {terminalIndex}. " +
                    "The terminal event ends the stream; LlmStream.Push drops anything later without " +
                    "complaint, so this content is lost rather than merely misordered."));
                continue;
            }

            switch (evt)
            {
                case StartEvent:
                    if (startSeen)
                    {
                        violations.Add(new EventOrderingViolation(
                            RuleSingleStart, i, "a second StartEvent was emitted; exactly one is permitted."));
                    }
                    else if (nonWarningSeen)
                    {
                        violations.Add(new EventOrderingViolation(
                            RuleStartPrecedesBlocks,
                            i,
                            $"StartEvent appeared at position {i}, after a non-warning event; it must " +
                            "precede every other event except a non-terminal warning."));
                    }

                    startSeen = true;
                    nonWarningSeen = true;
                    continue;

                case DoneEvent:
                case ErrorEvent:
                    terminalIndex = i;
                    terminalIsError = evt is ErrorEvent;
                    nonWarningSeen = true;
                    continue;

                case WarningEvent:
                    // Non-terminal and position-neutral (#3291). It opens and closes no block, so it
                    // has no place in the block grammar, and it may legitimately appear before the
                    // StartEvent when the very first frame is the abnormal one.
                    continue;
            }

            nonWarningSeen = true;

            var (kind, phase, contentIndex) = Classify(evt);
            if (kind is null)
                continue;

            if (!startSeen)
            {
                violations.Add(new EventOrderingViolation(
                    RuleStartPrecedesBlocks,
                    i,
                    $"'{evt.Type}' was emitted before any StartEvent."));
            }

            switch (phase)
            {
                case BlockPhase.Start:
                    if (openBlocks.TryGetValue(contentIndex, out var alreadyOpen))
                    {
                        violations.Add(new EventOrderingViolation(
                            RuleDuplicateStart,
                            i,
                            $"'{evt.Type}' opened content index {contentIndex}, which already has an " +
                            $"open '{alreadyOpen}' block. A block must be closed before the index is reused."));
                    }
                    else
                    {
                        openBlocks[contentIndex] = kind;
                    }

                    break;

                case BlockPhase.Delta:
                    if (!openBlocks.TryGetValue(contentIndex, out var deltaKind))
                    {
                        violations.Add(new EventOrderingViolation(
                            RuleDeltaWithoutStart,
                            i,
                            $"'{evt.Type}' arrived at content index {contentIndex} with no preceding " +
                            $"{kind}_start at that index."));
                    }
                    else if (deltaKind != kind)
                    {
                        violations.Add(new EventOrderingViolation(
                            RuleKindMismatch,
                            i,
                            $"'{evt.Type}' arrived at content index {contentIndex}, which holds an open " +
                            $"'{deltaKind}' block."));
                    }

                    break;

                case BlockPhase.End:
                    if (!openBlocks.TryGetValue(contentIndex, out var endKind))
                    {
                        violations.Add(new EventOrderingViolation(
                            RuleEndWithoutStart,
                            i,
                            $"'{evt.Type}' closed content index {contentIndex}, which has no open block. " +
                            "Either the start was never emitted or the block was already closed."));
                    }
                    else if (endKind != kind)
                    {
                        violations.Add(new EventOrderingViolation(
                            RuleKindMismatch,
                            i,
                            $"'{evt.Type}' closed content index {contentIndex}, which holds an open " +
                            $"'{endKind}' block."));
                        openBlocks.Remove(contentIndex);
                    }
                    else
                    {
                        openBlocks.Remove(contentIndex);
                    }

                    break;
            }
        }

        if (terminalIndex < 0)
        {
            violations.Add(new EventOrderingViolation(
                RuleMissingTerminal,
                events.Count,
                "the stream ended without a DoneEvent or ErrorEvent; a consumer awaiting the result " +
                "would wait forever."));
        }

        // An unclosed block is only a violation on the success path. A turn that errored or was
        // cancelled mid-block legitimately never emits the closing event - demanding one there would
        // assert a shape no producer can honour, which is how ordering tests become noise.
        if (!terminalIsError)
        {
            foreach (var (contentIndex, kind) in openBlocks.OrderBy(pair => pair.Key))
            {
                violations.Add(new EventOrderingViolation(
                    RuleUnclosedBlock,
                    events.Count,
                    $"content index {contentIndex} opened a '{kind}' block that was never closed " +
                    "before the stream completed successfully."));
            }
        }

        return violations;
    }

    private enum BlockPhase
    {
        Start,
        Delta,
        End
    }

    private static (string? Kind, BlockPhase Phase, int ContentIndex) Classify(AssistantMessageEvent evt) => evt switch
    {
        TextStartEvent e => ("text", BlockPhase.Start, e.ContentIndex),
        TextDeltaEvent e => ("text", BlockPhase.Delta, e.ContentIndex),
        TextEndEvent e => ("text", BlockPhase.End, e.ContentIndex),
        ThinkingStartEvent e => ("thinking", BlockPhase.Start, e.ContentIndex),
        ThinkingDeltaEvent e => ("thinking", BlockPhase.Delta, e.ContentIndex),
        ThinkingEndEvent e => ("thinking", BlockPhase.End, e.ContentIndex),
        ToolCallStartEvent e => ("toolcall", BlockPhase.Start, e.ContentIndex),
        ToolCallDeltaEvent e => ("toolcall", BlockPhase.Delta, e.ContentIndex),
        ToolCallEndEvent e => ("toolcall", BlockPhase.End, e.ContentIndex),
        _ => (null, BlockPhase.Start, 0)
    };
}
