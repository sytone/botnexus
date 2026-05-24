namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Atomic snapshot of a session's history together with the destructive-mutation
/// version observed when the snapshot was taken. Returned by
/// <see cref="GatewaySessionRuntime.SnapshotHistoryForCompaction"/> and passed
/// back via <see cref="GatewaySessionRuntime.TryReplaceHistoryFromSnapshot"/>
/// so the runtime can detect concurrent mutations and pick the safe apply path.
/// </summary>
/// <param name="Entries">Defensive copy of the history at snapshot time. Safe to enumerate freely.</param>
/// <param name="DestructiveVersion">Destructive-mutation counter observed under the runtime lock at snapshot time.</param>
/// <param name="Count">Number of entries in the snapshot (equal to <see cref="Entries"/>.Count).</param>
public sealed record HistorySnapshot(
    IReadOnlyList<SessionEntry> Entries,
    long DestructiveVersion,
    int Count);
