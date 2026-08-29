using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Conversations;

/// <summary>
/// A durable pending <c>ask_user</c> checkpoint, projected to the two fields startup
/// reconciliation actually needs: which conversation holds it, and the raw checkpoint JSON.
/// </summary>
/// <remarks>
/// <para>
/// Exists so <c>AskUserCheckpointReconciliationService</c> can rebuild the waiter map without
/// materialising the whole conversation population (issue #3660). Reconciliation previously
/// called <see cref="IConversationStore.ListAsync"/> and discarded all but a handful of rows on
/// the first line of the loop body — on a live store that was 3,964 fully-materialised
/// conversations to find 3 checkpoints, delaying the Kestrel port bind by ~3.5 minutes.
/// </para>
/// <para>
/// The projection is deliberately narrow. Widening it re-introduces the coupling between startup
/// cost and total conversation count that this type exists to sever, so anything needing more of
/// the conversation should fetch it by id via <see cref="IConversationStore.GetAsync"/>.
/// </para>
/// </remarks>
/// <param name="ConversationId">The conversation holding the pending checkpoint.</param>
/// <param name="PendingAskUserJson">
/// The raw, non-empty durable checkpoint payload. Never <c>null</c> or empty — implementations
/// filter empty payloads out rather than surfacing them, so callers do not repeat the check.
/// </param>
public readonly record struct PendingAskUserCheckpoint(
    ConversationId ConversationId,
    string PendingAskUserJson);
