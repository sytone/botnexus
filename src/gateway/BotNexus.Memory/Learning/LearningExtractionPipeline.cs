using BotNexus.Memory.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Memory.Learning;

/// <summary>
/// Extracts durable knowledge from indexed memory entries using the turn classifier
/// and routes results to appropriate stores via routing rules.
/// Integrates with the dreaming cron infrastructure for batch processing.
/// </summary>
public sealed class LearningExtractionPipeline
{
    private readonly IReadOnlyList<KnowledgeRoutingRule> _routingRules;
    private readonly ILogger _logger;

    public LearningExtractionPipeline(
        IReadOnlyList<KnowledgeRoutingRule> routingRules,
        ILogger logger)
    {
        _routingRules = routingRules ?? throw new ArgumentNullException(nameof(routingRules));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Processes memory entries from a session, classifying each turn pair and extracting
    /// durable knowledge. Returns extracted items with routing decisions applied.
    /// </summary>
    /// <param name="entries">Memory entries to process (typically from a single session).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of extracted knowledge items with routing decisions.</returns>
    public Task<IReadOnlyList<ExtractedKnowledge>> ExtractAsync(
        IReadOnlyList<MemoryEntry> entries,
        CancellationToken ct = default)
    {
        var extracted = new List<ExtractedKnowledge>();
        var router = new KnowledgeRouter(_routingRules);

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.SourceType != "conversation")
                continue;

            // Parse user/assistant from the stored format "User: ...\nAssistant: ..."
            if (!TryParseConversationEntry(entry.Content, out var userContent, out var assistantContent))
                continue;

            var classification = TurnClassifier.Classify(userContent, assistantContent);

            if (!classification.IsDurable || classification.Category is null)
                continue;

            var knowledge = new ExtractedKnowledge
            {
                Content = assistantContent,
                Category = classification.Category.Value,
                Confidence = classification.Confidence,
                SourceSessionId = entry.SessionId ?? string.Empty,
                SourceTurnIndex = entry.TurnIndex ?? 0,
                TargetStore = null,
                // The distilled item inherits the origin of the row it was distilled from, so a
                // quarantined transcript row cannot shed its provenance by passing through
                // extraction (#3232 AC3). Content is consulted as well as the column because a
                // quarantined row carries its marker in the text.
                ContributingProvenances = [ContributingProvenanceOf(entry)],
            };

            extracted.Add(knowledge);
        }

        // Apply routing rules
        var routed = router.RouteAll(extracted);

        _logger.LogInformation(
            "Learning extraction: {TotalEntries} entries processed, {DurableCount} durable items extracted, {RoutedCount} routed to shared stores.",
            entries.Count,
            routed.Count,
            routed.Count(k => k.TargetStore is not null));

        return Task.FromResult(routed);
    }

    /// <summary>
    /// The provenance an extracted item inherits from its source row.
    /// </summary>
    /// <remarks>
    /// Derived through <see cref="MemoryEntry.TrustTier"/> rather than read straight off the
    /// column, so an in-content quarantine marker (#2519) on a row whose column says otherwise
    /// still downgrades the extracted item. Mapping tier back to a vocabulary value keeps
    /// <see cref="ExtractedKnowledge.ContributingProvenances"/> expressed in the one closed
    /// vocabulary rather than introducing a parallel representation.
    /// </remarks>
    private static string ContributingProvenanceOf(MemoryEntry entry)
        => entry.TrustTier switch
        {
            MemoryTrustTier.Quarantined => MemoryProvenance.ExternalUntrusted,
            MemoryTrustTier.Untrusted => MemoryProvenance.Unknown,
            _ => entry.NormalizedProvenance,
        };

    /// <summary>
    /// Parses a stored turn-pair row back into its two role records.
    /// </summary>
    /// <remarks>
    /// Decoding goes through <see cref="TranscriptTurnFormat"/>, the same seam the writers encode with, so
    /// the reader can never disagree with the writer about where a role record ends (#2954). The helper
    /// also handles legacy undelimited rows written before that change.
    /// </remarks>
    internal static bool TryParseConversationEntry(string content, out string userContent, out string assistantContent)
        => TranscriptTurnFormat.TryDecode(content, out userContent, out assistantContent);
}
