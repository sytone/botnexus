using BotNexus.Domain.Primitives;
using BotNexus.Domain.Text;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Sessions;

/// <summary>
/// Canonical projections from full session history into the slices the LLM runtime
/// actually sees. Two predicates that look similar but are <em>deliberately</em>
/// distinct: one for cold-start resume hydration, one for in-flight token budgeting.
/// Both isolation strategies and the compactor must route through this type so the
/// rules cannot drift.
/// </summary>
/// <remarks>
/// Phase 3b (#534). Before this type existed, the resume filter lived inline in
/// <c>InProcessIsolationStrategy</c> and the budget filter lived as a private
/// predicate on <c>LlmSessionCompactor</c>. Any future isolation strategy that
/// hydrated a session would have had to re-invent the rules.
/// </remarks>
public static class SessionContextProjector
{
    /// <summary>
    /// Strict filter applied when hydrating a cold-resumed session into the initial
    /// LLM message list. Excludes:
    /// <list type="bullet">
    /// <item><see cref="SessionEntry.IsHistory"/> = true (folded into a later summary).</item>
    /// <item><see cref="SessionEntry.IsCrashSentinel"/> = true (recovery placeholder).</item>
    /// <item>Raw <see cref="MessageRole.System"/> entries (the agent's system prompt is
    /// rebuilt separately by <c>SystemPromptBuilder</c>); compaction-summary system
    /// entries are kept because they carry the folded context, and #3046 restart-replay
    /// banners are kept because they carry the only signal telling the agent its prior
    /// work was interrupted at an unknown point.</item>
    /// <item><see cref="MessageRole.Tool"/> entries. On cold-start resume, the
    /// Assistant <see cref="SessionEntry"/> only persists the response text and not
    /// the structured <c>tool_use</c> blocks, so the following Tool entry would
    /// become an orphaned <c>tool_result</c> with no paired call — Anthropic rejects
    /// that. In live continuous operation Tool entries are present in the agent's
    /// in-memory message list and are sent correctly; that is the
    /// <see cref="IsVisibleInLiveContext"/> case.</item>
    /// </list>
    /// </summary>
    public static bool IsVisibleOnResume(SessionEntry entry)
    {
        if (entry.IsCrashSentinel || entry.IsHistory)
            return false;

        return entry.Role.Equals(MessageRole.User)
            || entry.Role.Equals(MessageRole.Assistant)
            || (entry.Role.Equals(MessageRole.System) && (entry.IsCompactionSummary || entry.IsReplayBanner));
    }

    /// <summary>
    /// Broad filter applied when estimating the in-flight LLM token budget for the
    /// <em>next</em> call in a continuous session. Counts every entry that will be
    /// sent on that call — Tool and non-summary System entries included, because in
    /// a continuous run they sit beside their paired Assistant <c>tool_use</c> (and
    /// the agent's system prompt) in the live message list. Excludes folded entries,
    /// crash placeholders, notifications, and host-owned tool-result budget notices
    /// that are retained only for transcript presentation.
    /// </summary>
    public static bool IsVisibleInLiveContext(SessionEntry entry) =>
        !entry.IsHistory && !entry.IsCrashSentinel && !entry.Role.Equals(MessageRole.Notification)
        && !string.Equals(entry.Kind?.Value, ToolResultBudgetNotice.Kind, StringComparison.Ordinal);

    /// <summary>
    /// The billable character cost of a single entry: every field of it that is serialised into the
    /// LLM message list, not just its rendered text (#3536).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because <see cref="IsVisibleInLiveContext"/> and the cost of what it selects were
    /// previously decided in different places. The predicate correctly selected Tool entries - which
    /// a continuous session really does send - while the compaction estimator summed
    /// <see cref="SessionEntry.Content"/> alone. On the session that motivated this,
    /// <see cref="SessionEntry.ToolArgs"/> was 69% of the visible context: the estimator reported
    /// ~403,891 tokens where ~1,290,071 were present, a 3.19x undercount, and a
    /// <c>tool-start</c> row carrying 27,354 characters of arguments and no content was costed at
    /// zero.
    /// </para>
    /// <para>
    /// Keeping this beside the visibility predicate is the point: <b>selecting an entry and costing
    /// it are one decision</b>. Any consumer that filters on <c>IsVisibleInLiveContext</c> must size
    /// entries with this helper, so a field added to <see cref="SessionEntry"/> that reaches the
    /// provider is accounted for in exactly one place instead of being silently free in some
    /// callers. <c>ToolArgsAndContentFenceArchitectureTests</c> enforces that pairing.
    /// </para>
    /// <para>
    /// <see cref="SessionEntry.ThinkingContent"/> is included for the same reason: providers that
    /// echo reasoning blocks back into the transcript charge for them. It was zero on the motivating
    /// session, so this is correctness for the general case rather than a fix for observed mass.
    /// </para>
    /// </remarks>
    /// <param name="entry">The entry to size. Null-safe on every field.</param>
    /// <returns>Total characters this entry contributes to the next provider call.</returns>
    public static long GetLiveContextCharCost(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return (long)(entry.Content?.Length ?? 0)
            + (entry.ToolArgs?.Length ?? 0)
            + (entry.ThinkingContent?.Length ?? 0);
    }

    /// <summary>
    /// The script-weighted token cost of a single entry, in <see cref="TokenEstimator"/> quarter-token
    /// units (#3655).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the same selection-and-cost pairing as <see cref="GetLiveContextCharCost"/> - the same
    /// three payload-bearing fields, charged in the same one place - measured in a unit that is not
    /// blind to script. A raw character count treats a Han ideograph and an ASCII letter as equal,
    /// which under-counts a CJK transcript by roughly four times and lets it exhaust its real context
    /// window while the estimate still reads as comfortably under threshold.
    /// </para>
    /// <para>
    /// Units, not tokens: callers sum across every visible entry and convert once with
    /// <see cref="TokenEstimator.TokensFromUnits"/>. Rounding per entry would discard up to three
    /// quarter-tokens each time and reintroduce a systematic under-count.
    /// </para>
    /// <para>
    /// <see cref="GetLiveContextCharCost"/> is retained rather than replaced: the #1599 bloat trigger
    /// measures a single entry's raw size, which is a genuinely different question from what a
    /// transcript costs in tokens.
    /// </para>
    /// </remarks>
    /// <param name="entry">The entry to size. Null-safe on every field.</param>
    /// <returns>Weighted token units this entry contributes to the next provider call.</returns>
    public static long GetLiveContextTokenUnits(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return TokenEstimator.WeightedCharUnits(entry.Content)
            + TokenEstimator.WeightedCharUnits(entry.ToolArgs)
            + TokenEstimator.WeightedCharUnits(entry.ThinkingContent);
    }

    /// <summary>
    /// Eager projection used by isolation strategies to build the initial LLM
    /// message list on cold-start resume. Materialised so callers cannot be
    /// surprised by deferred enumeration over a <see cref="GatewaySession.History"/>
    /// list that is mutated concurrently.
    /// </summary>
    public static IReadOnlyList<SessionEntry> ProjectForResume(IEnumerable<SessionEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history.Where(IsVisibleOnResume).ToList();
    }

    /// <summary>
    /// Eager projection used by the compactor when sizing the token budget. Same
    /// materialisation rationale as <see cref="ProjectForResume"/>.
    /// </summary>
    public static IReadOnlyList<SessionEntry> ProjectForLiveContext(IEnumerable<SessionEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history.Where(IsVisibleInLiveContext).ToList();
    }

    /// <summary>
    /// Detects whether the session history contains an abandoned turn — a sequence of
    /// tool-start entries without matching tool-end entries that precedes the most recent
    /// user message. This indicates a prior turn stalled mid-execution and the dangling
    /// context should not be replayed to the agent.
    /// </summary>
    /// <remarks>
    /// A ToolStart entry is identified by <see cref="SessionEntry.IsToolStartRow"/> - the typed
    /// <c>MessageKind.ToolStart</c> stamp added in #2906, falling back for legacy rows to the old
    /// heuristic of having <see cref="SessionEntry.ToolArgs"/> set
    /// (populated from the <c>AgentStreamEventType.ToolStart</c> event). A matching ToolEnd
    /// shares the same <see cref="SessionEntry.ToolCallId"/> and execution identity.
    /// All earlier live turns are checked, including interruptions that predate multiple resumes.
    /// Calls in the latest turn are not declared abandoned, and completions flushed after its user
    /// message still resolve their original start.
    /// </remarks>
    public static AbandonedTurnResult DetectAbandonedTurn(IReadOnlyList<SessionEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        if (history.Count < 2)
            return AbandonedTurnResult.None;

        // Find the index of the most recent user message (the new inbound message).
        int lastUserIndex = -1;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (history[i].Role.Equals(MessageRole.User) && !history[i].IsHistory)
            {
                lastUserIndex = i;
                break;
            }
        }

        if (lastUserIndex <= 0)
            return AbandonedTurnResult.None;

        var abandoned = GetToolCallSpans(history)
            .Where(span => span.StartIndex is { } start && start < lastUserIndex && span.ResultIndex is null)
            .Select(span => history[span.StartIndex.GetValueOrDefault()])
            .ToArray();
        if (abandoned.Length == 0)
            return AbandonedTurnResult.None;

        return new AbandonedTurnResult(HasAbandonedTurn: true, AbandonedEntryCount: abandoned.Length)
        {
            AbandonedToolStarts = abandoned
        };
    }

    /// <summary>
    /// Pairs live tool rows chronologically within an execution. A reused identity after a completed
    /// exchange starts a new span, never supplies a result for a later pending call. Orphaned results
    /// and overlapping duplicate starts remain explicit so replay can reject ambiguous evidence.
    /// </summary>
    public static IReadOnlyList<(int? StartIndex, int? ResultIndex, bool Ambiguous)> GetToolCallSpans(
        IReadOnlyList<SessionEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var spans = new List<(int? StartIndex, int? ResultIndex, bool Ambiguous)>();
        var pending = new Dictionary<(AgentRunId? Run, string Id), int>();
        for (var i = 0; i < history.Count; i++)
        {
            var entry = history[i];
            if (entry.IsHistory || entry.IsCrashSentinel || entry.Role != MessageRole.Tool
                || string.IsNullOrEmpty(entry.ToolCallId))
                continue;
            var key = (entry.AgentRunId, entry.ToolCallId);
            if (entry.IsToolStartRow())
            {
                var ambiguous = pending.TryGetValue(key, out var previous);
                if (ambiguous)
                    spans[previous] = (spans[previous].StartIndex, spans[previous].ResultIndex, true);
                pending[key] = spans.Count;
                spans.Add((i, null, ambiguous));
            }
            else if (pending.Remove(key, out var start))
            {
                spans[start] = (spans[start].StartIndex, i, spans[start].Ambiguous);
            }
            else
            {
                spans.Add((null, i, false));
            }
        }
        return spans;
    }
}

/// <summary>
/// Result of abandoned turn detection.
/// </summary>
/// <param name="HasAbandonedTurn">True if dangling tool calls were found in the previous turn.</param>
/// <param name="AbandonedEntryCount">Number of tool-start entries without matching completions.</param>
public sealed record AbandonedTurnResult(bool HasAbandonedTurn, int AbandonedEntryCount)
{
    /// <summary>The original unfinished rows; recovery preserves their payloads in historical audit.</summary>
    public IReadOnlyList<SessionEntry> AbandonedToolStarts { get; init; } = [];

    /// <summary>Singleton for the no-abandoned-turn case.</summary>
    public static readonly AbandonedTurnResult None = new(HasAbandonedTurn: false, AbandonedEntryCount: 0);
}
