using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// World-id stamping/back-fill and citizen-matching logic shared by every
/// <see cref="BotNexus.Gateway.Abstractions.Conversations.IConversationStore"/> implementation.
/// <para>
/// These routines were previously copy-pasted byte-for-byte across <see cref="SqliteConversationStore"/>,
/// <see cref="FileConversationStore"/> and <see cref="InMemoryConversationStore"/> with source comments
/// noting they were "kept identical so the three stores behave the same way" — a standing invitation for
/// silent drift in world/citizen scoping semantics (#1383, Finding 3). Hoisting them into one place makes
/// the equivalence structural rather than a manual discipline; the store parity tests guard the behaviour.
/// </para>
/// </summary>
internal static class ConversationStoreShared
{
    /// <summary>
    /// Stamps the current world id onto a conversation being persisted (Create/Save). Only fills an
    /// empty <see cref="Conversation.WorldId"/> — explicit non-empty values are preserved so cross-world
    /// relays can hold the source world's id even when this gateway is the receiver. No-op when no world
    /// context is wired (e.g. test setups using the parameterless ctor).
    /// </summary>
    public static void StampWorldId(Conversation conversation, IWorldContext? worldContext)
    {
        if (string.IsNullOrEmpty(conversation.WorldId) && worldContext is not null)
            conversation.WorldId = worldContext.CurrentWorldId;
    }

    /// <summary>
    /// Read-time projection: legacy rows/sidecars persisted before #613 carry an empty world id. This
    /// projects them as belonging to the current world on the way out without rewriting the stored value —
    /// the next save round-trip durably persists it via <see cref="StampWorldId"/>. Treating backfill as
    /// projection-only keeps the read path single-pass and avoids touching the backing store on every read.
    /// </summary>
    public static Conversation? BackfillWorldId(Conversation? conversation, IWorldContext? worldContext)
    {
        if (conversation is not null && string.IsNullOrEmpty(conversation.WorldId) && worldContext is not null)
            conversation.WorldId = worldContext.CurrentWorldId;
        return conversation;
    }

    /// <summary>
    /// Determines whether a conversation belongs to the given citizen for <c>ListForCitizen</c> scoping:
    /// the citizen is the initiator, owns the conversation (agent-species only), or is a participant.
    /// </summary>
    public static bool MatchesCitizen(Conversation conversation, CitizenId citizen)
    {
        if (conversation.Initiator is { IsValid: true } init && init == citizen)
            return true;

        // Owner-match: only agent-species citizens own conversations.
        if (citizen.Kind == CitizenKind.Agent && citizen.AsAgent is { } agent && conversation.AgentId == agent)
            return true;

        // Participant-match (P9-F): the conversation includes this citizen in its participant set.
        if (conversation.Participants.Any(p => p.CitizenId == citizen))
            return true;

        return false;
    }
}
