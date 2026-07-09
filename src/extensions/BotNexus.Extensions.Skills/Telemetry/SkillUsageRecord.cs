namespace BotNexus.Extensions.Skills.Telemetry;

/// <summary>
/// A per-skill usage telemetry record (#1833, PBI6). Tracks how often a skill has been
/// listed/viewed, loaded, and patched, alongside provenance (<see cref="CreatedBy"/>) and a
/// <see cref="Pinned"/> flag that a later curator uses to protect a skill from archival.
/// </summary>
/// <remarks>
/// The counters are intentionally coarse and monotonic: they answer "which skills earn their
/// context budget" rather than reconstructing an audit trail. <see cref="LastUsedAt"/> is the
/// single freshness signal a future consolidation/curation pass keys off to mark stale
/// agent-created skills.
/// </remarks>
public sealed record SkillUsageRecord
{
    /// <summary>The skill name (directory / frontmatter name). Primary key for the telemetry row.</summary>
    public required string SkillName { get; init; }

    /// <summary>Number of times the skill was surfaced in a listing or had a support file viewed.</summary>
    public long ViewCount { get; init; }

    /// <summary>Number of times the skill was explicitly loaded into context.</summary>
    public long UseCount { get; init; }

    /// <summary>Number of times the skill was mutated via the skill-manage tool (patch/edit/write).</summary>
    public long PatchCount { get; init; }

    /// <summary>When the skill was last viewed, loaded, or patched; <c>null</c> if never touched.</summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>
    /// Identity that created the skill (agent id) when it was created through the manage tool,
    /// or <c>null</c> for skills that predate telemetry / were authored on disk directly.
    /// </summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// When true, the skill is protected from a future curator's archive/delete pass. Defaults
    /// to false; the curator itself is deferred (PBI6 ships telemetry only).
    /// </summary>
    public bool Pinned { get; init; }
}
