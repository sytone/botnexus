using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Outcome of a narrow, atomic session mutation (issue #2132). Narrow mutations replace the
/// read-modify-write-the-whole-aggregate pattern for metadata and lifecycle edits, so callers
/// need a way to distinguish "applied", "the row is gone", and "someone else got there first"
/// without inspecting a re-read aggregate.
/// </summary>
public enum SessionMutationOutcome
{
    /// <summary>The mutation was applied and is durable.</summary>
    Applied = 0,

    /// <summary>
    /// No session row exists for the requested id. Narrow mutations never create a row, so a
    /// caller that observes this must surface a 404 rather than retrying - retrying would only
    /// re-observe the same absence.
    /// </summary>
    NotFound = 1,

    /// <summary>
    /// The mutation was refused because the authoritative row is not in a state the caller's
    /// request permits: a lifecycle compare-and-set whose expected status no longer holds, or a
    /// transcript append against a session that has since been sealed or expired. This is the
    /// explicit conflict signal - the caller must re-read and decide, never blind-retry.
    /// </summary>
    Conflict = 2
}

/// <summary>
/// Result of <see cref="ISessionStore.PatchMetadataAsync"/> (issue #2132). Carries the metadata
/// as it stands <em>after</em> the merge so an API caller can echo the authoritative state back
/// without a second read that could observe yet another concurrent write.
/// </summary>
/// <param name="Outcome">Whether the patch applied, found no row, or conflicted.</param>
/// <param name="Metadata">
/// The post-merge metadata. Empty when <paramref name="Outcome"/> is not
/// <see cref="SessionMutationOutcome.Applied"/>.
/// </param>
public readonly record struct SessionMetadataMutationResult(
    SessionMutationOutcome Outcome,
    IReadOnlyDictionary<string, object?> Metadata)
{
    /// <summary>Creates a not-found result with no metadata.</summary>
    public static SessionMetadataMutationResult NotFound { get; } =
        new(SessionMutationOutcome.NotFound, new Dictionary<string, object?>());

    /// <summary>Creates a conflict result with no metadata.</summary>
    public static SessionMetadataMutationResult Conflict { get; } =
        new(SessionMutationOutcome.Conflict, new Dictionary<string, object?>());
}

/// <summary>
/// Result of <see cref="ISessionStore.TransitionStatusAsync"/> (issue #2132). On conflict the
/// <see cref="Status"/> reports the authoritative status observed under the store lock, which is
/// what a controller needs to render a meaningful 409 body ("cannot suspend a session in 'Sealed'
/// state") without an extra racy read.
/// </summary>
/// <param name="Outcome">Whether the transition applied, found no row, or conflicted.</param>
/// <param name="Status">
/// The session status after the call: the new status when applied, or the authoritative current
/// status when the compare-and-set was refused.
/// </param>
/// <param name="UpdatedAt">The row's updated timestamp after the call.</param>
public readonly record struct SessionStatusMutationResult(
    SessionMutationOutcome Outcome,
    SessionStatus Status,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Result of <see cref="ISessionStore.AppendEntriesAsync"/> (issue #2132).
/// </summary>
/// <param name="Outcome">Whether the append applied, found no row, or conflicted.</param>
/// <param name="AppendedCount">
/// How many entries were durably appended. Zero unless <paramref name="Outcome"/> is
/// <see cref="SessionMutationOutcome.Applied"/>.
/// </param>
public readonly record struct SessionAppendMutationResult(
    SessionMutationOutcome Outcome,
    int AppendedCount);
