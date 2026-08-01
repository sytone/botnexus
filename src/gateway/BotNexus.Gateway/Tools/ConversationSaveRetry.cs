using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Shared compare-and-swap retry policy for agent tools that persist a whole
/// <see cref="Conversation"/> aggregate through <see cref="IConversationStore.SaveAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Since #2471 the SQLite store guards <c>SaveAsync</c> with a version compare-and-swap and throws
/// <see cref="ConversationConcurrencyException"/> instead of silently clobbering a concurrent
/// writer. No tool handled that exception, so the store turned silent data loss into a raw
/// exception surfaced to the agent mid-turn (#2131). This helper is the single place that policy
/// lives - three hand-written loops would be exactly the drift #2131 is about.
/// </para>
/// <para>
/// The contract that makes this safe is that callers supply a <c>mutate</c> delegate rather than a
/// finished aggregate. On conflict the helper re-reads the conversation <em>fresh</em> from the
/// store and re-invokes <c>mutate</c> against that fresh state. Re-saving the caller's original
/// snapshot with a refreshed version number would defeat the CAS guard entirely and reintroduce the
/// data loss #2471 closed, so the stale candidate is never reused.
/// </para>
/// </remarks>
internal static class ConversationSaveRetry
{
    /// <summary>
    /// Total save attempts, including the first. Bounded so a hot conversation can never spin
    /// forever; four attempts comfortably absorbs the realistic contention (a portal pin or a
    /// canvas write landing between a tool's read and its save) without masking a genuinely
    /// pathological write storm.
    /// </summary>
    internal const int MaxAttempts = 4;

    /// <summary>
    /// Applies <paramref name="mutate"/> to <paramref name="current"/> and persists the result,
    /// re-reading and re-applying the mutation against fresh state if another writer committed
    /// first. Returns the aggregate that was actually persisted, or <see langword="null"/> when
    /// <paramref name="mutate"/> declined to produce a change (a no-op save is skipped entirely).
    /// </summary>
    /// <param name="store">The conversation store to persist through.</param>
    /// <param name="conversationId">The conversation being mutated; used to re-read on conflict.</param>
    /// <param name="current">The caller's already-loaded snapshot, used for the first attempt.</param>
    /// <param name="mutate">
    /// Recomputes the desired aggregate from whatever state is currently committed. This is invoked
    /// again with freshly-read state on every retry, so it must derive its result from its argument
    /// and must not close over values read from an earlier snapshot that the concurrent writer may
    /// have changed.
    /// </param>
    /// <param name="cancellationToken">Token to observe.</param>
    /// <exception cref="ConversationConcurrencyException">
    /// Thrown when every attempt lost the compare-and-swap race. Surfaced rather than swallowed so
    /// the caller can report an actionable failure instead of silently dropping the write.
    /// </exception>
    public static async Task<Conversation?> SaveWithRetryAsync(
        IConversationStore store,
        ConversationId conversationId,
        Conversation current,
        Func<Conversation, Conversation?> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(mutate);

        var candidate = mutate(current);

        for (var attempt = 1; ; attempt++)
        {
            if (candidate is null)
                return null;

            try
            {
                await store.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
                return candidate;
            }
            catch (ConversationConcurrencyException)
            {
                // Exhausted: never swallow. The exception message already names the conversation
                // and both versions, which is the actionable detail the agent needs.
                if (attempt >= MaxAttempts)
                    throw;

                // Re-read and recompute. The stale candidate is deliberately discarded: replaying it
                // against a refreshed version would overwrite the concurrent writer's columns.
                var fresh = await store.GetAsync(conversationId, cancellationToken).ConfigureAwait(false);
                if (fresh is null)
                    return null; // conversation was deleted underneath us; nothing left to write.

                candidate = mutate(fresh);
            }
        }
    }
}
