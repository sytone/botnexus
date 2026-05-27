namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Result of <see cref="GatewaySessionRuntime.AddEntryAndSnapshot"/>. Bundles
/// the post-append <see cref="HistorySnapshot"/> with the
/// <see cref="Session.UpdatedAt"/> value sampled <em>immediately before</em>
/// the append — both captured under the same runtime lock as the append.
/// Callers that need to restore the prior UpdatedAt after a slow follow-on
/// operation (e.g. heartbeat ack-prune) must use
/// <see cref="PriorUpdatedAt"/> rather than reading
/// <see cref="GatewaySession.UpdatedAt"/> before the call — the latter is
/// race-prone if a concurrent destructive mutation lands between the read and
/// the lock.
/// </summary>
/// <param name="Snapshot">Post-append snapshot. The appended entry is at <c>Snapshot.Count - 1</c>.</param>
/// <param name="PriorUpdatedAt">Pre-append <see cref="Session.UpdatedAt"/>, captured under the runtime lock.</param>
public sealed record SessionAppendResult(
    HistorySnapshot Snapshot,
    DateTimeOffset PriorUpdatedAt);
