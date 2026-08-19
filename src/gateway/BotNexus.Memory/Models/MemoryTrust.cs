namespace BotNexus.Memory.Models;

/// <summary>
/// The trust level a memory row carries at read time, derived from its
/// <see cref="MemoryProvenance"/> rather than stored alongside it (#3232).
/// </summary>
/// <remarks>
/// Ordered least-trusted-last is deliberately <b>not</b> how this is declared; the numeric
/// ordering runs most-trusted-first so <c>Math.Max</c> over a set of tiers yields the
/// least-trusted member. That makes "a summary is only as trustworthy as its worst
/// contributor" an arithmetic property rather than a rule every caller must remember.
/// </remarks>
public enum MemoryTrustTier
{
    /// <summary>First-party human instruction from the agent's own owner. Canon-eligible.</summary>
    Trusted = 0,

    /// <summary>The agent's own reasoning, or a tool result it executed. Canon-eligible via grooming.</summary>
    Derived = 1,

    /// <summary>
    /// Provenance is <i>unestablished</i>. Retrievable and down-weighted, never auto-promoted.
    /// Distinct from <see cref="Quarantined"/>: this is the pre-provenance corpus, not hostile content.
    /// </summary>
    Untrusted = 2,

    /// <summary>
    /// Provenance is <i>established as hostile-capable</i> - third-party text the agent does not
    /// control. Retrievable on explicit request, never injected, never promoted.
    /// </summary>
    Quarantined = 3,
}

/// <summary>
/// The pure derivation from <see cref="MemoryProvenance"/> to <see cref="MemoryTrustTier"/>, and
/// the policy the rest of the memory subsystem consumes at rank, injection and promotion time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived, never persisted (#3232 AC2).</b> There is no trust column. A stored tier could
/// drift from the provenance it was computed from - through a partial migration, a direct SQL
/// edit, or simply a write path that forgot to recompute it - and a drifted trust value fails
/// <i>open</i>, presenting untrusted content as first-party. Recomputing on every read costs a
/// switch statement and makes that class of bug unrepresentable.
/// </para>
/// <para>
/// <b>Weighting, not filtering (#3232 AC4).</b> Non-first-party rows are multiplied down in the
/// ranking, not dropped before it. A pre-rank filter makes untrusted content silently vanish: the
/// caller cannot tell the difference between "the store holds nothing about X" and "the store
/// holds only untrusted material about X", and those demand opposite responses. Down-weighting
/// keeps the row discoverable, explainable, and clearly labelled.
/// </para>
/// <para>
/// <b>Least-trusted wins on mixture (#3232 AC3).</b> A summary distilled from several rows takes
/// the worst contributing tier, never the most common one. Majority-voting a mixture erases the
/// single hostile contributor that is precisely the reason to be looking.
/// </para>
/// </remarks>
public static class MemoryTrust
{
    /// <summary>Rank multiplier for a tier that may be weighed as the agent's own knowledge.</summary>
    public const double FirstPartyWeight = 1.0d;

    /// <summary>
    /// Rank multiplier for <see cref="MemoryTrustTier.Untrusted"/>.
    /// </summary>
    /// <remarks>
    /// Chosen to demote rather than bury. An unestablished row must still win against a genuinely
    /// weak first-party match, because the ~22.8k pre-provenance rows are the agent's real history
    /// and a coefficient near zero would amount to the destructive backfill this issue forbids.
    /// </remarks>
    public const double UntrustedWeight = 0.6d;

    /// <summary>
    /// Rank multiplier for <see cref="MemoryTrustTier.Quarantined"/>.
    /// </summary>
    /// <remarks>
    /// Strictly below <see cref="UntrustedWeight"/> because the two tiers mean different things:
    /// unestablished versus established-as-hostile-capable. Still strictly above zero - a
    /// quarantined row remains reachable for an operator auditing what the store is holding, which
    /// is impossible if the ranker has already discarded it.
    /// </remarks>
    public const double QuarantinedWeight = 0.25d;

    /// <summary>
    /// Maps a provenance value to exactly one tier. Total over the closed vocabulary, and total
    /// over arbitrary input because it normalises first: an unrecognised, null or malformed value
    /// resolves to <see cref="MemoryTrustTier.Untrusted"/> rather than throwing or defaulting to
    /// a trusted tier (#3232 AC1, AC9).
    /// </summary>
    public static MemoryTrustTier Derive(string? provenance)
        => MemoryProvenance.Normalize(provenance) switch
        {
            MemoryProvenance.User => MemoryTrustTier.Trusted,
            MemoryProvenance.Agent or MemoryProvenance.Tool => MemoryTrustTier.Derived,
            MemoryProvenance.ExternalUntrusted => MemoryTrustTier.Quarantined,
            _ => MemoryTrustTier.Untrusted,
        };

    /// <summary>
    /// Derives the tier for stored content, honouring the in-content quarantine marker as well as
    /// the provenance column.
    /// </summary>
    /// <remarks>
    /// Defence in depth against a laundering path that provenance alone cannot close. Markdown
    /// daily notes have no provenance column at all - the #2519 marker embedded in the text is
    /// their only origin record - and a row could be re-inserted by a future write path that
    /// copies content while stamping a fresh first-party provenance. Taking the least-trusted of
    /// the two signals means the marker cannot be outvoted by a column.
    /// </remarks>
    public static MemoryTrustTier DeriveFromContent(string? provenance, string? content)
    {
        var fromProvenance = Derive(provenance);
        return Tools.MemoryQuarantine.IsQuarantined(content)
            ? LeastTrusted(fromProvenance, MemoryTrustTier.Quarantined)
            : fromProvenance;
    }

    /// <summary>
    /// Whether the tier may be weighed as the agent's own knowledge - and therefore whether it may
    /// be injected into always-on context or promoted into a shared store.
    /// </summary>
    public static bool IsFirstParty(MemoryTrustTier tier)
        => tier is MemoryTrustTier.Trusted or MemoryTrustTier.Derived;

    /// <summary>The rank coefficient applied to the blended hybrid score for this tier.</summary>
    public static double RankWeight(MemoryTrustTier tier)
        => tier switch
        {
            MemoryTrustTier.Trusted or MemoryTrustTier.Derived => FirstPartyWeight,
            MemoryTrustTier.Untrusted => UntrustedWeight,
            _ => QuarantinedWeight,
        };

    /// <summary>The less trusted of two tiers.</summary>
    public static MemoryTrustTier LeastTrusted(MemoryTrustTier first, MemoryTrustTier second)
        => (MemoryTrustTier)Math.Max((int)first, (int)second);

    /// <summary>
    /// Resolves the tier of content derived from several source provenances to the least-trusted
    /// contributor (#3232 AC3).
    /// </summary>
    /// <remarks>
    /// An empty contributing set resolves to <see cref="MemoryTrustTier.Untrusted"/>, not to a
    /// trusted tier: "we recorded no contributors" is an unestablished origin by definition, and
    /// is exactly the shape a summariser that forgot to record its sources would produce.
    /// </remarks>
    public static MemoryTrustTier LeastTrusted(IEnumerable<string?> provenances)
    {
        ArgumentNullException.ThrowIfNull(provenances);

        var resolved = MemoryTrustTier.Trusted;
        var any = false;

        foreach (var provenance in provenances)
        {
            any = true;
            resolved = LeastTrusted(resolved, Derive(provenance));
        }

        return any ? resolved : MemoryTrustTier.Untrusted;
    }

    /// <summary>
    /// The provenance value that a derived/summarised row must be stamped with, given its
    /// contributing source provenances.
    /// </summary>
    /// <remarks>
    /// Returns the actual least-trusted contributing <i>value</i>, not a canonical representative
    /// of its tier. Mapping <c>tool</c> onto <c>agent</c> because both are first-party would be a
    /// re-stamp - it upgrades a tool result into the agent's own words while leaving the tier
    /// unchanged, and a reader of the promoted row could no longer tell which it was. Ties within
    /// a tier resolve to the first contributor, so the result is deterministic for a given input.
    /// An empty set yields <see cref="MemoryProvenance.Unknown"/> rather than a first-party value.
    /// </remarks>
    public static string ResolveDerivedProvenance(IEnumerable<string?> contributingProvenances)
    {
        ArgumentNullException.ThrowIfNull(contributingProvenances);

        string? worstValue = null;
        var worstTier = MemoryTrustTier.Trusted;

        foreach (var provenance in contributingProvenances)
        {
            var normalized = MemoryProvenance.Normalize(provenance);
            var tier = Derive(normalized);

            if (worstValue is null || tier > worstTier)
            {
                worstValue = normalized;
                worstTier = tier;
            }
        }

        return worstValue ?? MemoryProvenance.Unknown;
    }

    /// <summary>The stable lowercase wire/display name for a tier.</summary>
    public static string ToWireValue(MemoryTrustTier tier)
        => tier switch
        {
            MemoryTrustTier.Trusted => "trusted",
            MemoryTrustTier.Derived => "derived",
            MemoryTrustTier.Untrusted => "untrusted",
            _ => "quarantined",
        };
}
