namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// Accumulated spend signals for a single conversation (#2898), derived at read time from the
/// session and transcript tables rather than from any stored counter (#2557 precedent).
/// </summary>
/// <remarks>
/// <para>
/// Every field that the platform cannot presently measure is <b>nullable, and a
/// <see langword="null"/> means "not measured" - never zero</b> (#2554 precedent). Coercing an
/// unmeasured signal to <c>0</c> would present "we did not look" as "this conversation is free",
/// which inverts precisely the ranking this type exists to produce.
/// </para>
/// <para>
/// There is deliberately no stored rollup: the counts are a <c>GROUP BY</c> over rows that already
/// exist, so they cannot drift from the transcript the way a maintained counter would.
/// </para>
/// </remarks>
/// <param name="ConversationId">The conversation these counts belong to.</param>
/// <param name="SessionCount">
/// How many sessions the conversation spans - the ramp signal. Always measurable, because the
/// session rows themselves are the evidence.
/// </param>
/// <param name="MessageCount">
/// How many transcript entries the conversation accumulated across all its sessions. Always
/// measurable for the same reason.
/// </param>
/// <param name="CompactionSummaryCount">
/// How many compaction summaries the conversation's sessions carry - the context-pressure signal -
/// or <see langword="null"/> when the configured session store exposes no transcript-free way to
/// count them. Null is <b>not</b> "no compactions": it is "this store did not measure it".
/// </param>
/// <param name="TotalTokens">
/// Total provider tokens attributed to the conversation, or <see langword="null"/> when no
/// provider-usage measurement exists for it. Null today for every conversation on a gateway with
/// no per-turn usage recorded, which is exactly why it must not read as a measured zero.
/// </param>
public sealed record ConversationCostSummary(
    string ConversationId,
    int SessionCount,
    int MessageCount,
    int? CompactionSummaryCount = null,
    long? TotalTokens = null);

/// <summary>
/// Optional session-store capability that answers the conversation cost rollup (#2898) with a
/// single aggregate query instead of hydrating transcripts.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a <b>separate optional interface</b> rather than a new member on
/// <c>ISessionStore</c>. Only a store with a query engine can count compaction summaries without
/// reading every transcript, so making this a required member would either force a ruinous
/// full-load default onto the File and InMemory stores or force fourteen hand-rolled test doubles
/// to grow a stub they cannot meaningfully implement.
/// </para>
/// <para>
/// A gateway whose store does not implement this degrades to the transcript-free session/message
/// counts available from <c>ISessionStore.ListSummariesAsync</c>, with
/// <see cref="ConversationCostSummary.CompactionSummaryCount"/> left <see langword="null"/> - the
/// honest answer, and the reason that field is nullable.
/// </para>
/// </remarks>
public interface IConversationCostReader
{
    /// <summary>
    /// Returns one cost rollup per conversation that owns at least one session.
    /// </summary>
    /// <remarks>
    /// Conversations with no sessions are absent from the result rather than present with zeroes;
    /// the caller supplies the zero-session row, because only the caller knows the full
    /// conversation set.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ConversationCostSummary>> GetConversationCostsAsync(
        CancellationToken cancellationToken = default);
}
