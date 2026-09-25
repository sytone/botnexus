using BotNexus.Gateway.Abstractions.Extensions;

namespace BotNexus.Gateway.Search;

/// <summary>
/// Reports one contributor's search outcome while retaining its source-owned ordering and metadata.
/// </summary>
/// <param name="SourceId">Stable contributor identifier.</param>
/// <param name="Label">Contributor label suitable for display.</param>
/// <param name="IsAvailable">Whether the contributor advertised itself as available for this request.</param>
/// <param name="CanAssessProvenanceTrust">Whether the contributor can make evidence-based trust assessments.</param>
/// <param name="Count">Number of results returned after applying the requested bound.</param>
/// <param name="Results">Results in contributor-local order.</param>
/// <param name="Error">Source-local failure description, or <see langword="null"/> after a successful search.</param>
public sealed record AggregatedSearchGroup(
    string SourceId,
    string Label,
    bool IsAvailable,
    bool CanAssessProvenanceTrust,
    int Count,
    IReadOnlyList<SearchResult> Results,
    string? Error);
