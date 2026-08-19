namespace BotNexus.Memory.Models;

public sealed record MemoryEntry
{
    public required string Id { get; init; }
    public required string AgentId { get; init; }
    public string? SessionId { get; init; }
    public int? TurnIndex { get; init; }
    public required string SourceType { get; init; } // conversation, manual, compaction, dreaming
    public required string Content { get; init; }
    public string? MetadataJson { get; init; }
    public byte[]? Embedding { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsArchived { get; init; }

    /// <summary>
    /// Where this entry's content came from - one of <see cref="MemoryProvenance"/> (#2480).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="SourceType"/>, which records what kind of write produced the row.
    /// Nullable because the column is additive: rows written before provenance existed carry no
    /// value and are read back as <see cref="MemoryProvenance.Unknown"/> via
    /// <see cref="NormalizedProvenance"/> rather than being backfilled destructively or rejected.
    /// </remarks>
    public string? Provenance { get; init; }

    /// <summary>Conversation the content originated in, when known. Nullable and additive.</summary>
    public string? OriginConversationId { get; init; }

    /// <summary>
    /// Session the content originated in, when it differs from <see cref="SessionId"/> - e.g. a
    /// consolidated or promoted row whose storage session is not the session that produced the text.
    /// </summary>
    public string? OriginSessionId { get; init; }

    /// <summary>
    /// The provenance coerced into the closed vocabulary, defaulting to
    /// <see cref="MemoryProvenance.Unknown"/>. Read paths should use this rather than the raw
    /// column so an absent, stale or malformed value can never present as first-party.
    /// </summary>
    public string NormalizedProvenance => MemoryProvenance.Normalize(Provenance);

    /// <summary>
    /// The trust tier derived from <see cref="Provenance"/> and the content's quarantine marker
    /// at read time (#3232).
    /// </summary>
    /// <remarks>
    /// Computed, never stored. A persisted tier column could drift from the provenance it was
    /// derived from, and a drifted trust value fails open - which is the one direction a trust
    /// signal must never fail in. See <see cref="MemoryTrust"/> for the derivation and its policy.
    /// </remarks>
    public MemoryTrustTier TrustTier => MemoryTrust.DeriveFromContent(Provenance, Content);

    /// <summary>
    /// Whether this row may be weighed as the agent's own knowledge - and therefore whether it is
    /// eligible for always-on context injection and automatic promotion (#3232).
    /// </summary>
    public bool IsFirstParty => MemoryTrust.IsFirstParty(TrustTier);
}
