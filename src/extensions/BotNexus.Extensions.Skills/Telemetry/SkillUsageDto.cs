namespace BotNexus.Extensions.Skills.Telemetry;

/// <summary>
/// API response body for the skill usage telemetry read surface (#1833): the full set of per-skill
/// usage rows exposed at <c>GET /api/skills/telemetry</c>.
/// </summary>
public sealed class SkillUsageTelemetryResponse
{
    /// <summary>The per-skill telemetry rows, most-recently-used first.</summary>
    public IReadOnlyList<SkillUsageDto> Skills { get; set; } = [];
}

/// <summary>
/// API projection of a single <see cref="SkillUsageRecord"/> for the telemetry read surface (#1833).
/// </summary>
public sealed class SkillUsageDto
{
    /// <summary>The skill name.</summary>
    public string SkillName { get; set; } = string.Empty;

    /// <summary>Times the skill was surfaced in a listing or had a support file viewed.</summary>
    public long ViewCount { get; set; }

    /// <summary>Times the skill was loaded into context.</summary>
    public long UseCount { get; set; }

    /// <summary>Times the skill was mutated via the manage tool.</summary>
    public long PatchCount { get; set; }

    /// <summary>When the skill was last viewed, loaded, or patched; <c>null</c> if never touched.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Agent id that created the skill through the manage tool, or <c>null</c>.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Whether the skill is protected from a future curator's archive/delete pass.</summary>
    public bool Pinned { get; set; }
}
