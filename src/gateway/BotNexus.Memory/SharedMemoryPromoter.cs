using BotNexus.Domain.Text;
using BotNexus.Memory.Learning;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Logging;

namespace BotNexus.Memory;

/// <summary>
/// Promotes extracted knowledge to shared memory stores during the dreaming cycle.
/// Handles deduplication by checking existing entries for content overlap.
/// </summary>
public sealed class SharedMemoryPromoter
{
    private readonly ISharedMemoryStoreRegistry _registry;
    private readonly ILogger _logger;

    /// <summary>Minimum similarity ratio (0-1) to consider an entry a duplicate.</summary>
    private const double DeduplicationThreshold = 0.85;

    public SharedMemoryPromoter(ISharedMemoryStoreRegistry registry, ILogger logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Promotes routed knowledge items to their target shared stores.
    /// Skips items that already exist (deduplication) or where the agent lacks write access.
    /// </summary>
    /// <param name="agentId">Agent performing the promotion.</param>
    /// <param name="items">Knowledge items with routing decisions already applied.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of items successfully promoted.</returns>
    public async Task<int> PromoteAsync(
        string agentId,
        IReadOnlyList<ExtractedKnowledge> items,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var promoted = 0;
        var refused = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            if (item.TargetStore is null)
                continue;

            // Promotion refusal (#3232 AC6). A shared store is read by agents that never saw the
            // originating turn and cannot judge the content's origin for themselves, so promotion
            // is an authority transfer, not a copy. Non-first-party material is refused rather
            // than promoted-and-labelled: the label would have to survive every future read path
            // to be worth anything, and one that forgets it reopens the laundering route this
            // issue exists to close. The content stays in the origin agent's own store.
            if (!item.IsPromotable)
            {
                refused++;
                _logger.LogWarning(
                    "Refusing to promote non-first-party knowledge to shared store '{Store}' for agent '{AgentId}': provenance={Provenance}, contributors=[{Contributors}]",
                    item.TargetStore,
                    agentId,
                    item.Provenance,
                    string.Join(", ", item.ContributingProvenances));
                continue;
            }

            if (!_registry.CanWrite(agentId, item.TargetStore))
            {
                _logger.LogWarning(
                    "Agent '{AgentId}' lacks write access to shared store '{Store}', skipping promotion",
                    agentId, item.TargetStore);
                continue;
            }

            var store = _registry.GetStore(item.TargetStore);
            if (store is null)
            {
                _logger.LogWarning(
                    "Shared store '{Store}' not found in registry, skipping promotion",
                    item.TargetStore);
                continue;
            }

            // Deduplication: search for similar content
            if (await IsDuplicateAsync(store, item.Content, ct).ConfigureAwait(false))
            {
                _logger.LogDebug(
                    "Skipping duplicate promotion to '{Store}': {ContentPreview}",
                    item.TargetStore, Truncate(item.Content, 80));
                continue;
            }

            var entry = new MemoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                AgentId = agentId,
                SessionId = item.SourceSessionId,
                TurnIndex = item.SourceTurnIndex,
                SourceType = "dreaming",
                // The promoted row carries the LEAST-TRUSTED contributing provenance, not a fresh
                // first-party stamp (#3232 AC3/AC7). Re-stamping on ingest is the exact laundering
                // step the trust boundary exists to prevent; the originating session is preserved
                // separately so the promoted row does not lose its trail (#2480).
                Provenance = item.Provenance,
                OriginSessionId = item.SourceSessionId,
                Content = item.Content,
                MetadataJson = BuildMetadataJson(item),
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await store.InsertAsync(entry, ct).ConfigureAwait(false);
            promoted++;

            _logger.LogInformation(
                "Promoted knowledge to shared store '{Store}': category={Category}, confidence={Confidence:F2}",
                item.TargetStore, item.Category, item.Confidence);
        }

        _logger.LogInformation(
            "Shared memory promotion complete: {Promoted}/{Total} items promoted for agent '{AgentId}' ({Refused} refused as non-first-party)",
            promoted, items.Count(i => i.TargetStore is not null), agentId, refused);

        return promoted;
    }

    /// <summary>
    /// Checks if content already exists in the target store (basic keyword overlap dedup).
    /// Uses search to find potential duplicates, then compares content similarity.
    /// </summary>
    internal async Task<bool> IsDuplicateAsync(IMemoryStore store, string content, CancellationToken ct)
    {
        // Extract key terms for search (first 100 chars as query)
        var query = Truncate(content, 100);

        try
        {
            var existing = await store.SearchAsync(query, topK: 5, ct: ct).ConfigureAwait(false);

            foreach (var entry in existing)
            {
                var similarity = ComputeJaccardSimilarity(content, entry.Content);
                if (similarity >= DeduplicationThreshold)
                    return true;
            }
        }
        catch (Exception ex)
        {
            // If search fails, allow promotion (don't block on dedup failure)
            _logger.LogDebug(ex, "Deduplication search failed, allowing promotion");
        }

        return false;
    }

    /// <summary>
    /// Computes Jaccard similarity between two texts based on word-level tokens.
    /// Returns 0.0 (no overlap) to 1.0 (identical word sets).
    /// </summary>
    internal static double ComputeJaccardSimilarity(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);

        if (setA.Count == 0 && setB.Count == 0)
            return 1.0;
        if (setA.Count == 0 || setB.Count == 0)
            return 0.0;

        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();

        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string text)
        => text.Split([' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length > 2)
            .ToHashSet();

    private static string Truncate(string text, int maxLength)
        => TextTruncation.SafeTruncate(text, maxLength)!;

    private static string BuildMetadataJson(ExtractedKnowledge item)
        => $"{{\"category\":\"{item.Category}\",\"confidence\":{item.Confidence:F2},\"sourceSession\":\"{item.SourceSessionId}\",\"sourceTurn\":{item.SourceTurnIndex}}}";
}
