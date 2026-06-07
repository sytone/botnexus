using BotNexus.Domain.Primitives;
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
    /// entries are kept because they carry the folded context.</item>
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
            || (entry.Role.Equals(MessageRole.System) && entry.IsCompactionSummary);
    }

    /// <summary>
    /// Broad filter applied when estimating the in-flight LLM token budget for the
    /// <em>next</em> call in a continuous session. Counts every entry that will be
    /// sent on that call — Tool and non-summary System entries included, because in
    /// a continuous run they sit beside their paired Assistant <c>tool_use</c> (and
    /// the agent's system prompt) in the live message list. Only entries that the
    /// compactor itself or a crash has already removed from the LLM view are
    /// excluded.
    /// </summary>
    public static bool IsVisibleInLiveContext(SessionEntry entry) =>
        !entry.IsHistory && !entry.IsCrashSentinel && !entry.Role.Equals(MessageRole.Notification);

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
    /// A ToolStart entry is identified by having <see cref="SessionEntry.ToolArgs"/> set
    /// (populated from the <c>AgentStreamEventType.ToolStart</c> event). A matching ToolEnd
    /// shares the same <see cref="SessionEntry.ToolCallId"/> but has no <c>ToolArgs</c>.
    /// The detection scans backward from the most recent user message and checks all tool
    /// entries in the preceding turn for unmatched starts.
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

        // Find the second-to-last user message (start of the previous turn).
        int prevUserIndex = -1;
        for (int i = lastUserIndex - 1; i >= 0; i--)
        {
            if (history[i].Role.Equals(MessageRole.User) && !history[i].IsHistory)
            {
                prevUserIndex = i;
                break;
            }
        }

        if (prevUserIndex < 0)
            return AbandonedTurnResult.None;

        // Scan the entries between prevUserIndex and lastUserIndex for dangling tool starts.
        // A ToolStart has ToolArgs set; a ToolEnd for the same call has the same ToolCallId
        // but no ToolArgs.
        var toolStarts = new HashSet<string>(StringComparer.Ordinal);
        var toolEnds = new HashSet<string>(StringComparer.Ordinal);
        int abandonedCount = 0;

        for (int i = prevUserIndex + 1; i < lastUserIndex; i++)
        {
            var entry = history[i];
            if (entry.IsCrashSentinel || entry.IsHistory)
                continue;

            if (!entry.Role.Equals(MessageRole.Tool))
                continue;

            if (entry.ToolCallId is null)
                continue;

            if (entry.ToolArgs is not null)
            {
                // This is a ToolStart entry
                toolStarts.Add(entry.ToolCallId);
            }
            else
            {
                // This is a ToolEnd entry
                toolEnds.Add(entry.ToolCallId);
            }
        }

        // Dangling starts = ToolStarts that have no matching ToolEnd
        foreach (var startId in toolStarts)
        {
            if (!toolEnds.Contains(startId))
                abandonedCount++;
        }

        if (abandonedCount == 0)
            return AbandonedTurnResult.None;

        return new AbandonedTurnResult(HasAbandonedTurn: true, AbandonedEntryCount: abandonedCount);
    }
}

/// <summary>
/// Result of abandoned turn detection.
/// </summary>
/// <param name="HasAbandonedTurn">True if dangling tool calls were found in the previous turn.</param>
/// <param name="AbandonedEntryCount">Number of tool-start entries without matching completions.</param>
public sealed record AbandonedTurnResult(bool HasAbandonedTurn, int AbandonedEntryCount)
{
    /// <summary>Singleton for the no-abandoned-turn case.</summary>
    public static readonly AbandonedTurnResult None = new(HasAbandonedTurn: false, AbandonedEntryCount: 0);
}
