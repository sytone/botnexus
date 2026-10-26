using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Contracts.Agents;

/// <summary>An agent and its ordinary conversation, independent of replaceable sessions.</summary>
/// <param name="AgentId">Owner authenticated by the caller's trusted boundary.</param>
/// <param name="ConversationId">Ordinary conversation provisioned for that owner.</param>
public sealed record PeerDialogueEndpoint(AgentId AgentId, ConversationId ConversationId);

/// <summary>Immutable creation inputs; expiry is absolute and the budget is dialogue-wide.</summary>
/// <param name="DialogueConversationId">Stable correlation identity, never a session identifier.</param>
/// <param name="First">First authenticated endpoint.</param>
/// <param name="Second">Distinct second authenticated endpoint.</param>
/// <param name="Origin">Endpoint whose authenticated agent may create this specification.</param>
/// <param name="MaxAutomaticMessages">Positive shared reservation ceiling.</param>
/// <param name="ExpiresAt">Absolute deadline; new creation requires a future instant.</param>
public sealed record PeerDialogueSpecification(ConversationId DialogueConversationId,
    PeerDialogueEndpoint First, PeerDialogueEndpoint Second, PeerDialogueEndpoint Origin,
    int MaxAutomaticMessages, DateTimeOffset ExpiresAt)
{
    /// <summary>Admission-boundary ceiling for existing agent and conversation identifier values.</summary>
    public const int MaxEndpointIdentityLength = 256;
}

/// <summary>Immutable snapshot of durable admission state, not agent execution state.</summary>
/// <param name="Specification">Original immutable creation inputs.</param>
/// <param name="ReservedTurns">Messages admitted in both directions combined.</param>
/// <param name="IsClosed">An explicit, irreversible admission stop.</param>
public sealed record PeerDialogue(PeerDialogueSpecification Specification, long ReservedTurns, bool IsClosed);

/// <summary>A message whose identity and exact payload are fixed by first admission.</summary>
/// <param name="MessageId">Globally unique client identity, also used for retry reconciliation.</param>
/// <param name="DialogueConversationId">Dialogue to charge for this message.</param>
/// <param name="Sender">Must match the separately authenticated caller and an immutable endpoint.</param>
/// <param name="Recipient">Must be the opposite immutable endpoint.</param>
/// <param name="ReplyTo">When present, opposite-author message addressed to this sender.</param>
/// <param name="Content">Nonblank bounded text; comparison is ordinal and exact, not trimmed.</param>
public sealed record PeerDialogueMessageRequest(PeerDialogueMessageId MessageId,
    ConversationId DialogueConversationId, PeerDialogueEndpoint Sender, PeerDialogueEndpoint Recipient,
    PeerDialogueMessageId? ReplyTo, string Content)
{
    /// <summary>Maximum UTF-16 code units retained per immutable message.</summary>
    public const int MaxContentLength = 16_384;
}

/// <summary>Admission does not execute anything; this foundation deliberately has no dispatcher.</summary>
public enum PeerDialogueDeliveryState
{
    /// <summary>Durably admitted, not evidence of execution or of an external side effect.</summary>
    Pending
}

/// <summary>Durable reservation and pending delivery, committed together.</summary>
/// <param name="Request">Original immutable identity and exact payload.</param>
/// <param name="Sequence">One-based dialogue-wide reservation sequence.</param>
/// <param name="AdmittedAt">Store clock instant of admission.</param>
/// <param name="DeliveryState">Admission evidence only; never exactly-once execution evidence.</param>
public sealed record PeerDialogueMessage(PeerDialogueMessageRequest Request, long Sequence,
    DateTimeOffset AdmittedAt, PeerDialogueDeliveryState DeliveryState);

/// <summary>Machine-readable rejection with no payload or foreign message content.</summary>
public enum PeerDialogueRejection
{
    /// <summary>Malformed specification, endpoint, content, reply or enumeration bounds.</summary>
    Invalid,
    /// <summary>Caller does not own the claimed endpoint or cannot access the dialogue.</summary>
    Unauthorized,
    /// <summary>Dialogue is absent.</summary>
    NotFound,
    /// <summary>Stable identity was reused with changed immutable inputs.</summary>
    Conflict,
    /// <summary>Explicit admission stop.</summary>
    Closed,
    /// <summary>Absolute deadline reached.</summary>
    Expired,
    /// <summary>All shared automatic turns are reserved.</summary>
    Exhausted
}

/// <summary>Privacy-preserving contract failure; neither IDs nor content appear in its message.</summary>
public sealed class PeerDialogueException : InvalidOperationException
{
    /// <summary>Creates a rejection without including untrusted payloads in diagnostics.</summary>
    public PeerDialogueException(PeerDialogueRejection reason) : base($"Peer dialogue operation rejected: {reason}.") => Reason = reason;

    /// <summary>Allows callers to handle rejection without parsing diagnostic text.</summary>
    public PeerDialogueRejection Reason { get; }
}

/// <summary>
/// Durable admission only. A trusted caller must authenticate agent identities and provision
/// ordinary endpoint conversations before invoking this store; the store validates immutable
/// membership, not credentials or conversation existence. No tool, producer, dispatcher or
/// duplicate agent runtime is enabled by this interface.
/// </summary>
public interface IPeerDialogueStore
{
    /// <summary>Creates once as the origin; identical replay returns current state even after expiry/close.</summary>
    Task<PeerDialogue> CreateAsync(AgentId authenticatedAgentId, PeerDialogueSpecification specification, CancellationToken cancellationToken = default);

    /// <summary>Returns state only to a member; does not expose message content.</summary>
    Task<PeerDialogue> GetAsync(AgentId authenticatedAgentId, ConversationId dialogueConversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reserves one shared turn and Pending delivery. Equal authenticated replay returns
    /// the original even after closure/expiry/exhaustion; changed or cross-dialogue replay conflicts.
    /// Cancellation before commit rolls back; an uncertain commit must be reconciled by stable ID.
    /// </summary>
    Task<PeerDialogueMessage> AdmitAsync(AgentId authenticatedAgentId, PeerDialogueMessageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Irreversibly and idempotently stops new admission, without deleting prior evidence.</summary>
    Task CloseAsync(AgentId authenticatedAgentId, ConversationId dialogueConversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists only messages addressed to the authenticated member, ordered by ascending sequence
    /// after the exclusive cursor. Limit must be 1..100. This is evidence enumeration, not permission
    /// to dispatch closed/expired work or replay uncertain execution. No execution acknowledgement exists here.
    /// </summary>
    Task<IReadOnlyList<PeerDialogueMessage>> ListPendingAsync(AgentId authenticatedAgentId,
        ConversationId dialogueConversationId, int limit, long afterSequence = 0, CancellationToken cancellationToken = default);
}
