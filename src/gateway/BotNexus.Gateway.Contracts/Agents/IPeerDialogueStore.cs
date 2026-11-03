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
    Pending,
    /// <summary>Owned but not started; lease expiry permits a new generation.</summary>
    Reserved,
    /// <summary>Start persisted before any runtime invocation; never automatically replay.</summary>
    Started,
    /// <summary>Actual result persisted, with any admitted reply in the same transaction.</summary>
    Completed,
    /// <summary>Execution may have produced effects; automatic replay is forbidden.</summary>
    Uncertain,
    /// <summary>Explicitly suppressed before start, preserving admission evidence.</summary>
    Cancelled
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
    Exhausted,
    /// <summary>Claim does not own the current live execution generation/state.</summary>
    StaleClaim
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
/// Durable admission and fenced delivery evidence. A trusted caller must authenticate agent identities and provision
/// ordinary endpoint conversations before invoking this store; the store validates immutable
/// membership, not credentials or conversation existence. No tool, producer, dispatcher or
/// duplicate agent runtime is enabled by this interface.
/// </summary>
public interface IPeerDialogueStore
{
    /// <summary>Reserves only executable pending work for the recipient, or returns null when already owned/terminal.
    /// Lease must be positive and at most five minutes. Expired unstarted reservations are reclaimable;
    /// expired Started work becomes Uncertain. Open/expiry is rechecked under the writer transaction.</summary>
    Task<PeerDialogueClaim?> ClaimAsync(AgentId authenticatedAgentId, ConversationId dialogueConversationId,
        PeerDialogueMessageId messageId, RunId owner, TimeSpan lease, CancellationToken cancellationToken = default);

    /// <summary>Persists the start boundary for the exact live claim after checking open/expiry again.
    /// This is one-use authorization: a Started retry is rejected. If commit is uncertain, inspect evidence;
    /// never invoke the runtime again merely because Start did not return successfully.</summary>
    Task<PeerDialogueDelivery> StartAsync(AgentId authenticatedAgentId, ConversationId dialogueConversationId,
        PeerDialogueClaim claim, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges only the current live Started owner. Atomically records actual output and admits
    /// at most one opposite-direction reply using one shared turn. Failure, closure, expiry or exhaustion
    /// never emits a reply. Equal completion retry reconciles; changed result conflicts. Cancellation rolls back all writes.</summary>
    Task<PeerDialogueDelivery> CompleteAsync(AgentId authenticatedAgentId, ConversationId dialogueConversationId,
        PeerDialogueClaim claim, PeerDialogueCompletion completion, CancellationToken cancellationToken = default);

    /// <summary>Returns member-scoped evidence, materializing expired Started work as Uncertain, never Pending.</summary>
    Task<PeerDialogueDelivery> GetDeliveryAsync(AgentId authenticatedAgentId, ConversationId dialogueConversationId,
        PeerDialogueMessageId messageId, CancellationToken cancellationToken = default);

    /// <summary>Either member may suppress unstarted work. Cancelling Started work records Uncertain,
    /// not proof of interruption of an external runtime; completed evidence remains unchanged.</summary>
    Task CancelAsync(AgentId authenticatedAgentId, ConversationId dialogueConversationId,
        PeerDialogueMessageId messageId, CancellationToken cancellationToken = default);

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
    /// to dispatch closed/expired work or replay uncertain execution. Use the fenced lifecycle, never this evidence list, to authorize execution.
    /// </summary>
    Task<IReadOnlyList<PeerDialogueMessage>> ListPendingAsync(AgentId authenticatedAgentId,
        ConversationId dialogueConversationId, int limit, long afterSequence = 0, CancellationToken cancellationToken = default);
}

/// <summary>Fenced execution reservation. Owner is a unique execution attempt, not an agent/session identity.</summary>
/// <param name="MessageId">Immutable admitted message to execute.</param>
/// <param name="Owner">Unique run owning this reservation.</param>
/// <param name="Generation">Monotonically increasing reservation fence for this message.</param>
/// <param name="LeaseExpiresAt">Absolute execution deadline; a start does not renew it.</param>
public sealed record PeerDialogueClaim(PeerDialogueMessageId MessageId, RunId Owner, long Generation, DateTimeOffset LeaseExpiresAt);

/// <summary>Actual execution evidence, distinct from a reply that may or may not be admitted.</summary>
/// <param name="Succeeded">Whether execution succeeded; failure never produces an automatic reply.</param>
/// <param name="Output">Actual authored output, retained verbatim even when not admissible as a reply.</param>
/// <param name="ReplyMessageId">Stable identity for the possible reply, used for atomic retry reconciliation.</param>
public sealed record PeerDialogueCompletion(bool Succeeded, string? Output, PeerDialogueMessageId ReplyMessageId);

/// <summary>Current execution evidence. Neither Completed nor Uncertain promises exactly-once external effects.</summary>
/// <param name="Message">Immutable admission evidence.</param>
/// <param name="State">Current execution lifecycle, separate from original admission.</param>
/// <param name="Claim">Last reservation fence, retained after terminal transitions.</param>
/// <param name="StartedAt">Durable boundary recorded before invoking the ordinary runtime.</param>
/// <param name="Completion">Actual result, if a live owner acknowledged execution.</param>
/// <param name="Reply">Reply admitted in the same transaction as completion, if permitted.</param>
/// <param name="ReplyRejection">Admission rejection, distinct from execution failure or absent output.</param>
public sealed record PeerDialogueDelivery(PeerDialogueMessage Message, PeerDialogueDeliveryState State,
    PeerDialogueClaim? Claim, DateTimeOffset? StartedAt, PeerDialogueCompletion? Completion,
    PeerDialogueMessage? Reply, PeerDialogueRejection? ReplyRejection);
