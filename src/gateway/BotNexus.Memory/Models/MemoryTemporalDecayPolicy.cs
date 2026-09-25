namespace BotNexus.Memory.Models;

/// <summary>
/// The effective temporal-decay policy applied by native memory retrieval.
/// </summary>
public sealed record MemoryTemporalDecayPolicy
{
    /// <summary>The default policy used when an agent does not configure temporal decay.</summary>
    public static MemoryTemporalDecayPolicy Default { get; } = new(enabled: true, halfLifeDays: 30d);

    /// <summary>Creates a validated policy.</summary>
    public MemoryTemporalDecayPolicy(bool enabled, double halfLifeDays)
    {
        if (!double.IsFinite(halfLifeDays) || halfLifeDays <= 0d)
            throw new ArgumentOutOfRangeException(nameof(halfLifeDays), "Memory temporal-decay half-life must be finite and greater than zero.");

        Enabled = enabled;
        HalfLifeDays = halfLifeDays;
    }

    /// <summary>Whether age contributes a rank penalty.</summary>
    public bool Enabled { get; }

    /// <summary>The age in days at which relevance is halved when decay is enabled.</summary>
    public double HalfLifeDays { get; }

    /// <summary>The exponential-decay coefficient consumed by the one native ranker.</summary>
    public double Lambda => Enabled ? Math.Log(2d) / HalfLifeDays : 0d;
}
