using BotNexus.Memory.Models;

namespace BotNexus.Memory.Learning;

/// <summary>
/// Categories for classifying durable knowledge extracted from conversations.
/// </summary>
public enum KnowledgeCategory
{
    Decision,
    Pattern,
    Fact,
    Procedure,
    Preference,
}

/// <summary>
/// Result of classifying a conversation turn pair as transient or durable.
/// </summary>
public sealed record ClassificationResult(
    bool IsDurable,
    KnowledgeCategory? Category,
    double Confidence);

/// <summary>
/// Extracted knowledge from a durable conversation turn.
/// </summary>
public sealed record ExtractedKnowledge
{
    public required string Content { get; init; }
    public required KnowledgeCategory Category { get; init; }
    public required double Confidence { get; init; }
    public required string SourceSessionId { get; init; }
    public required int SourceTurnIndex { get; init; }
    public string? TargetStore { get; init; }

    /// <summary>
    /// The provenance of every source entry this item was distilled from (#3232 AC3).
    /// </summary>
    /// <remarks>
    /// Recorded as the full contributing set rather than a single collapsed value, because the
    /// collapse is lossy and the audit question - "what went into this row?" - cannot be answered
    /// from the result. <see cref="Provenance"/> performs the collapse on demand; this keeps the
    /// evidence for it.
    /// </remarks>
    public IReadOnlyList<string> ContributingProvenances { get; init; } = [];

    /// <summary>
    /// The provenance this item must be stamped with: the least-trusted contributing value.
    /// </summary>
    /// <remarks>
    /// Least-trusted, never most-common. A summary that mixed agent reasoning with a hostile issue
    /// body is exactly as trustworthy as the issue body, and majority-voting the mixture would
    /// erase the single contributor that is the entire reason to be looking. An item with no
    /// recorded contributors resolves to <c>unknown</c> rather than to a first-party value.
    /// </remarks>
    public string Provenance => MemoryTrust.ResolveDerivedProvenance(ContributingProvenances);

    /// <summary>Whether this item is eligible for automatic promotion into a shared store (#3232 AC6).</summary>
    public bool IsPromotable => MemoryTrust.IsFirstParty(MemoryTrust.Derive(Provenance));
}

/// <summary>
/// A routing rule that determines whether extracted knowledge should be promoted
/// to a shared store based on category and confidence threshold.
/// </summary>
public sealed record KnowledgeRoutingRule
{
    /// <summary>Category to match. Null means match all categories.</summary>
    public KnowledgeCategory? Category { get; init; }

    /// <summary>Minimum confidence score required for promotion (0.0–1.0).</summary>
    public double MinConfidence { get; init; } = 0.7;

    /// <summary>Target shared store name to promote to.</summary>
    public required string TargetStore { get; init; }
}
