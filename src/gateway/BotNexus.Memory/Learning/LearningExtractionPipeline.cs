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
    /// Parses the "User: ...\nAssistant: ..." format stored by MemoryIndexer.
    /// </summary>
    internal static bool TryParseConversationEntry(string content, out string userContent, out string assistantContent)
    {
        userContent = string.Empty;
        assistantContent = string.Empty;

        const string userPrefix = "User: ";
        const string assistantPrefix = "\nAssistant: ";

        if (!content.StartsWith(userPrefix, StringComparison.Ordinal))
            return false;

        var assistantIndex = content.IndexOf(assistantPrefix, StringComparison.Ordinal);
        if (assistantIndex < 0)
            return false;

        userContent = content[userPrefix.Length..assistantIndex];
        assistantContent = content[(assistantIndex + assistantPrefix.Length)..];
        return true;
    }
}
