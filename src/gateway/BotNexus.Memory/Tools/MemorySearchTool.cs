using BotNexus.Domain.Text;
using System.Globalization;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Memory.Tools;

/// <summary>
/// Searches the agent's persistent memory via the <see cref="IAgentMemory"/> abstraction.
/// Results are ranked by relevance with optional temporal decay.
/// Supports cross-store search via <see cref="ISharedMemoryStoreRegistry"/> when configured.
/// </summary>
public sealed class MemorySearchTool : IAgentTool
{
    private readonly IAgentMemory _agentMemory;
    private readonly string _agentId;
    private readonly int _defaultTopK;
    private readonly int _maxTopK;
    private readonly ISharedMemoryStoreRegistry? _sharedRegistry;

    public MemorySearchTool(IAgentMemory agentMemory, string agentId, MemoryAgentConfig? config = null, ISharedMemoryStoreRegistry? sharedRegistry = null)
    {
        _agentMemory = agentMemory ?? throw new ArgumentNullException(nameof(agentMemory));
        _agentId = string.IsNullOrWhiteSpace(agentId)
            ? throw new ArgumentException("Agent ID is required.", nameof(agentId))
            : agentId;
        _defaultTopK = Math.Max(1, config?.Search?.DefaultTopK ?? 10);
        _maxTopK = Math.Max(_defaultTopK, config?.Search?.MaxTopK ?? 100);
        _sharedRegistry = sharedRegistry;
    }

    public string Name => "memory_search";

    public string Label => "Memory Search";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Searches the agent's own memory store; see MemoryGetTool on per-entry markers.</summary>
    public string ContentSource => ToolContentSource.Local;

    public Tool Definition => new(
        Name,
        "Search the agent's persistent memory across all past sessions. Use natural language queries to find relevant past conversations, decisions, facts, and stored knowledge. Results are ranked by relevance with optional temporal decay (recent memories score higher).",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "query": {
                  "type": "string",
                  "description": "Natural language search query"
                },
                "topK": {
                  "type": "integer",
                  "description": "Maximum number of results to return (default: 10). Values above the configured ceiling are clamped."
                },
                "scope": {
                  "type": "string",
                  "description": "Search scope: 'own' (agent's private store only), 'shared' (shared stores only), or 'all' (both). Default: 'all'",
                  "enum": ["own", "shared", "all"]
                },
                "store": {
                  "type": "string",
                  "description": "Specific shared store name to search. When set, only that store is searched regardless of scope."
                },
                "minScore": {
                  "type": "number",
                  "description": "Optional relevance floor. Results whose fused relevance score is below this value are excluded, so a query with no genuinely relevant match returns an empty set instead of a ranked list of near-misses. Scores are provider-specific magnitudes, not a fixed 0-1 scale: calibrate against observed values rather than assuming a threshold."
                },
                "filter": {
                  "type": "object",
                  "description": "Optional filters: { sourceType, sessionId, afterDate, beforeDate, tags }",
                  "properties": {
                    "sourceType": { "type": "string" },
                    "sessionId": { "type": "string" },
                    "afterDate": { "type": "string" },
                    "beforeDate": { "type": "string" },
                    "tags": { "type": "array", "items": { "type": "string" } }
                  }
                }
              },
              "required": ["query"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!arguments.TryGetValue("query", out var queryValue) || string.IsNullOrWhiteSpace(ToStringValue(queryValue)))
            throw new ArgumentException("Missing required argument: query.");

        var prepared = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["query"] = ToStringValue(queryValue)!
        };

        if (arguments.TryGetValue("topK", out var topK) && topK is not null)
            prepared["topK"] = ToIntValue(topK, "topK");

        if (arguments.TryGetValue("scope", out var scope) && scope is not null)
            prepared["scope"] = ToStringValue(scope);

        if (arguments.TryGetValue("store", out var store) && store is not null)
            prepared["store"] = ToStringValue(store);

        if (arguments.TryGetValue("minScore", out var minScore) && minScore is not null)
            prepared["minScore"] = ToDoubleValue(minScore, "minScore");

        if (arguments.TryGetValue("filter", out var filter) && filter is not null)
            prepared["filter"] = filter;

        return Task.FromResult<IReadOnlyDictionary<string, object?>>(prepared);
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        return ExecuteCoreAsync(arguments, cancellationToken);
    }

    private async Task<AgentToolResult> ExecuteCoreAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var query = ToStringValue(arguments["query"]) ?? string.Empty;
        var topK = arguments.TryGetValue("topK", out var topKValue) && topKValue is not null
            ? Math.Clamp(ToIntValue(topKValue, "topK"), 1, _maxTopK)
            : _defaultTopK;
        var scope = arguments.TryGetValue("scope", out var scopeValue) && scopeValue is not null
            ? ToStringValue(scopeValue) ?? "all"
            : "all";
        var storeName = arguments.TryGetValue("store", out var storeValue) && storeValue is not null
            ? ToStringValue(storeValue)
            : null;
        var minScore = arguments.TryGetValue("minScore", out var minScoreValue) && minScoreValue is not null
            ? ToDoubleValue(minScoreValue, "minScore")
            : (double?)null;
        var filter = ParseFilter(arguments.TryGetValue("filter", out var filterValue) ? filterValue : null);

        // If a specific store is requested, validate access and search only that store
        if (!string.IsNullOrWhiteSpace(storeName))
        {
            return await SearchSpecificStoreAsync(query, topK, storeName!, filter, minScore, cancellationToken).ConfigureAwait(false);
        }

        var allResults = new List<AgentMemorySearchResult>();

        // Search own store
        if (scope is "own" or "all")
        {
            var request = new AgentMemorySearchRequest(
                AgentId: _agentId,
                Query: query,
                TopK: topK,
                Filter: filter);

            var ownResults = await _agentMemory.SearchAsync(request, cancellationToken).ConfigureAwait(false);
            allResults.AddRange(ownResults);
        }

        // Search shared stores
        if ((scope is "shared" or "all") && _sharedRegistry is not null)
        {
            var readableStores = _sharedRegistry.GetReadableStores(_agentId);
            var memoryFilter = filter is not null
                ? new MemorySearchFilter
                {
                    SourceType = filter.SourceType,
                    SessionId = filter.SessionId,
                    AfterDate = filter.AfterDate,
                    BeforeDate = filter.BeforeDate,
                    Tags = filter.Tags
                }
                : null;

            foreach (var name in readableStores)
            {
                var store = _sharedRegistry.GetStore(name);
                if (store is null) continue;

                var sharedResults = await store.SearchScoredAsync(query, topK, memoryFilter, cancellationToken).ConfigureAwait(false);
                foreach (var scored in sharedResults)
                {
                    allResults.Add(new AgentMemorySearchResult(
                        Id: scored.Entry.Id,
                        Content: scored.Entry.Content,
                        SourceType: $"shared:{name}",
                        SessionId: scored.Entry.SessionId,
                        CreatedAt: scored.Entry.CreatedAt,
                        RelevanceScore: scored.Score)
                    {
                        Provenance = scored.Entry.NormalizedProvenance,
                        OriginConversationId = scored.Entry.OriginConversationId,
                        OriginSessionId = scored.Entry.OriginSessionId
                    });
                }
            }
        }

        // Order by the ranker's fused relevance score, then apply the caller's floor. The floor is
        // applied AFTER ranking so it filters the same magnitude that is rendered - a pre-ranking
        // filter would threshold un-normalised per-store signals and mean something different again.
        var finalResults = ApplyFloorAndRank(allResults, minScore, topK);

        if (finalResults.Count == 0)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "No matching memories found.")]);

        return FormatResults(finalResults);
    }

    private async Task<AgentToolResult> SearchSpecificStoreAsync(
        string query, int topK, string storeName,
        AgentMemorySearchFilter? filter, double? minScore, CancellationToken cancellationToken)
    {
        if (_sharedRegistry is null)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Shared memory stores are not configured.")]);

        if (!_sharedRegistry.CanRead(_agentId, storeName))
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Access denied: agent '{_agentId}' cannot read from store '{storeName}'.")]);

        var store = _sharedRegistry.GetStore(storeName);
        if (store is null)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, $"Store '{storeName}' not found.")]);

        var memoryFilter = filter is not null
            ? new MemorySearchFilter
            {
                SourceType = filter.SourceType,
                SessionId = filter.SessionId,
                AfterDate = filter.AfterDate,
                BeforeDate = filter.BeforeDate,
                Tags = filter.Tags
            }
            : null;

        var entries = await store.SearchScoredAsync(query, topK, memoryFilter, cancellationToken).ConfigureAwait(false);
        var results = ApplyFloorAndRank(
            entries.Select(scored => new AgentMemorySearchResult(
                Id: scored.Entry.Id,
                Content: scored.Entry.Content,
                SourceType: $"shared:{storeName}",
                SessionId: scored.Entry.SessionId,
                CreatedAt: scored.Entry.CreatedAt,
                RelevanceScore: scored.Score)
            {
                Provenance = scored.Entry.NormalizedProvenance,
                OriginConversationId = scored.Entry.OriginConversationId,
                OriginSessionId = scored.Entry.OriginSessionId
            }),
            minScore,
            topK);

        if (results.Count == 0)
            return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, "No matching memories found.")]);

        return FormatResults(results);
    }

    /// <summary>
    /// Applies the optional relevance floor to already-ranked results and takes the caller's slice.
    /// </summary>
    /// <remarks>
    /// Deliberately does not recompute relevance: it orders and thresholds the score the ranker
    /// already produced. A second relevance definition in the display path is exactly the drift
    /// #2781 exists to remove. Filtering precedes <c>Take</c> so a floor cannot yield a short page
    /// padded out of a larger candidate set - when nothing clears the floor the set is empty.
    /// </remarks>
    private static List<AgentMemorySearchResult> ApplyFloorAndRank(
        IEnumerable<AgentMemorySearchResult> results,
        double? minScore,
        int topK)
        => results
            .Where(result => minScore is not { } floor || result.RelevanceScore >= floor)
            .OrderByDescending(result => result.RelevanceScore)
            .ThenByDescending(result => result.CreatedAt)
            .Take(topK)
            .ToList();

    private static AgentToolResult FormatResults(IReadOnlyList<AgentMemorySearchResult> entries)
    {
        var lines = new List<string>(entries.Count * 6)
        {
            $"Found {entries.Count} memory entr{(entries.Count == 1 ? "y" : "ies")}:",
            string.Empty
        };

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            // Defence-in-depth: neutralize any control / role-injection markup in historical rows
            // (written before sanitization existed) on the recall path too (#1560).
            var sanitized = UntrustedContentSanitizer.Sanitize(entry.Content);
            var preview = TextTruncation.SafeTruncate(sanitized, 240, "...")!;
            preview = preview.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ');

            lines.Add($"[{i + 1}] ID: {entry.Id}");
            // Rank AND magnitude: the ordinal alone told a caller nothing about whether the top hit
            // was any good, which is the whole of #2781.
            lines.Add($"Score: {entry.RelevanceScore.ToString("0.####", CultureInfo.InvariantCulture)} (rank #{i + 1})");
            lines.Add($"Timestamp: {entry.CreatedAt:O}");
            if (!string.IsNullOrWhiteSpace(entry.SessionId))
                lines.Add($"Session: {entry.SessionId}");
            lines.Add($"Source: {entry.SourceType}");
            // #2480: origin, not just kind. Without this a summarised GitHub issue body reads back
            // on a later session as first-party agent knowledge with its provenance erased.
            lines.Add($"Provenance: {entry.Provenance}");
            lines.Add($"Preview: {preview}");
            lines.Add(string.Empty);
        }

        return new AgentToolResult([new AgentToolContent(AgentToolContentType.Text, string.Join(Environment.NewLine, lines))]);
    }

    private static AgentMemorySearchFilter? ParseFilter(object? value)
    {
        if (value is null)
            return null;

        JsonElement element = value switch
        {
            JsonElement jsonElement => jsonElement,
            string text when !string.IsNullOrWhiteSpace(text) => JsonDocument.Parse(text).RootElement.Clone(),
            _ => JsonSerializer.SerializeToElement(value)
        };

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return new AgentMemorySearchFilter
        {
            SourceType = ReadOptionalString(element, "sourceType"),
            SessionId = ReadOptionalString(element, "sessionId"),
            AfterDate = ReadOptionalDate(element, "afterDate"),
            BeforeDate = ReadOptionalDate(element, "beforeDate"),
            Tags = ReadOptionalStringArray(element, "tags")
        };
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static DateTimeOffset? ReadOptionalDate(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(property.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string>? ReadOptionalStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return null;

        List<string> tags = [];
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                tags.Add(item.GetString()!);
        }

        return tags.Count == 0 ? null : tags;
    }

    private static string? ToStringValue(object? value)
        => value switch
        {
            null => null,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.ToString(),
            _ => value.ToString()
        };

    private static int ToIntValue(object value, string argumentName)
        => value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            double d => (int)d,
            JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element when int.TryParse(element.GetString(), out var parsed) => parsed,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var d) => (int)d,
            string text when int.TryParse(text, out var parsed) => parsed,
            string text when double.TryParse(text, out var d) => (int)d,
            _ => throw new ArgumentException($"Argument '{argumentName}' must be an integer.")
        };

    /// <summary>
    /// Coerces a relevance-floor argument to a double across the shapes a provider may send it in
    /// (native number, JSON number, or a numeric string).
    /// </summary>
    private static double ToDoubleValue(object value, string argumentName)
        => value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element
                when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ArgumentException($"Argument '{argumentName}' must be a number.")
        };
}
