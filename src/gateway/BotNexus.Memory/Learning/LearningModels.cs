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
