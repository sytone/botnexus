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
}
