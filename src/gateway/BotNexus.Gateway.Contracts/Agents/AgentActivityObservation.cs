namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Immutable, payload-free activity at the agent-handle isolation boundary. The two identity words
/// carry a fixed-size digest of normalized tool identity and result evidence, not invocation IDs,
/// arguments, raw errors, paths, or model prose. Identical evidence must have identical identities
/// across invocations; success/failure is distinguished by Kind. Turn boundaries use zero identity.
/// This contract carries observations only; it does not implement detection or evidence hashing.
/// </summary>
/// <param name="Kind">The authoritative tool outcome or completed-turn boundary.</param>
/// <param name="IdentityHigh">The first half of the internally computed evidence digest.</param>
/// <param name="IdentityLow">The second half of the internally computed evidence digest.</param>
public sealed record AgentActivityObservation(AgentActivityKind Kind, ulong IdentityHigh, ulong IdentityLow);

/// <summary>Only authoritative completion events participate in no-progress policy.</summary>
public enum AgentActivityKind
{
    /// <summary>A completed successful tool; a previously unseen digest constitutes new evidence.</summary>
    ToolSucceeded,
    /// <summary>A completed failing tool; its digest identifies normalized failing activity.</summary>
    ToolFailed,
    /// <summary>All completed tool observations for this turn have been delivered.</summary>
    TurnCompleted,
    /// <summary>
    /// A successful tool whose result cannot prove unchanged external progress. Every observation
    /// resets the no-progress window, even when its identity repeats; it is not deduplicated evidence.
    /// </summary>
    ToolProgressUnknown
}
