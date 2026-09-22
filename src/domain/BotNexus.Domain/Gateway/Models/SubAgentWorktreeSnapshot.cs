using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Bounded recovery evidence captured from a caller-granted Git worktree after an interrupted
/// sub-agent run. Untracked files are named only; their contents are never copied.
/// </summary>
/// <param name="Outcome">The explicit result of the best-effort snapshot attempt.</param>
/// <param name="WorktreePath">The selected granted Git worktree, when one qualified.</param>
/// <param name="ArtifactPath">The retained patch path, present only for a captured tracked diff.</param>
/// <param name="PatchBytes">UTF-8 patch size, or zero when no artifact was written.</param>
/// <param name="UntrackedPaths">Bounded repository-relative names of untracked files.</param>
/// <param name="UntrackedPathsTruncated">Whether additional untracked names were omitted.</param>
public sealed record SubAgentWorktreeSnapshot(
    SubAgentWorktreeSnapshotOutcome Outcome,
    string? WorktreePath,
    string? ArtifactPath,
    int PatchBytes,
    IReadOnlyList<string> UntrackedPaths,
    bool UntrackedPathsTruncated);

/// <summary>Describes every bounded outcome of a sub-agent recovery snapshot attempt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubAgentWorktreeSnapshotOutcome>))]
public enum SubAgentWorktreeSnapshotOutcome
{
    /// <summary>No caller-granted writable path was available to inspect.</summary>
    NoWriteGrant,
    /// <summary>None of the granted paths existed or could be accessed.</summary>
    NoAccessibleGrant,
    /// <summary>No granted directory was itself a Git worktree root.</summary>
    NoGitWorktree,
    /// <summary>The selected worktree had no tracked changes.</summary>
    Clean,
    /// <summary>A bounded tracked patch was retained.</summary>
    Captured,
    /// <summary>The tracked patch exceeded the configured hard byte ceiling.</summary>
    Oversized,
    /// <summary>The snapshot deadline elapsed.</summary>
    TimedOut,
    /// <summary>A Git subprocess failed.</summary>
    ProcessFailed,
    /// <summary>The artifact could not be stored safely.</summary>
    ArtifactWriteFailed
}
