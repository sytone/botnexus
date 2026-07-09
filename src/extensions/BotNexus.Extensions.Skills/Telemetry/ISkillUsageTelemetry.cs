namespace BotNexus.Extensions.Skills.Telemetry;

/// <summary>
/// Records and reads skill usage telemetry (#1833). Implementations must be safe to call from the
/// agent-facing skill tools on the hot path; recording is best-effort and must never throw back
/// into a tool execution (the tools swallow telemetry failures so a telemetry outage cannot break
/// skill loading). The counters are upserted per skill name.
/// </summary>
public interface ISkillUsageTelemetry
{
    /// <summary>Increments the skill's view counter and refreshes <c>last_used_at</c>. Used when a skill is listed or a support file is viewed.</summary>
    Task RecordViewAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>Increments the skill's use counter and refreshes <c>last_used_at</c>. Used when a skill is loaded into context.</summary>
    Task RecordUseAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>Increments the skill's patch counter and refreshes <c>last_used_at</c>. Used when a skill is mutated via the manage tool.</summary>
    Task RecordPatchAsync(string skillName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures a telemetry row exists for a newly created skill and stamps <c>created_by</c>. Used
    /// when a skill is created through the manage tool so provenance is captured for later curation.
    /// </summary>
    Task RecordCreatedAsync(string skillName, string createdBy, CancellationToken cancellationToken = default);

    /// <summary>Sets (or clears) the pinned flag that protects a skill from a future curator's archive/delete pass.</summary>
    Task SetPinnedAsync(string skillName, bool pinned, CancellationToken cancellationToken = default);

    /// <summary>Returns all telemetry rows ordered by most-recently-used first for the admin/API surface.</summary>
    Task<IReadOnlyList<SkillUsageRecord>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the telemetry row for a single skill, or <c>null</c> if the skill has no recorded activity.</summary>
    Task<SkillUsageRecord?> GetAsync(string skillName, CancellationToken cancellationToken = default);
}
