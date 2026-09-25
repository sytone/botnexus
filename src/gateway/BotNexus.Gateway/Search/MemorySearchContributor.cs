using BotNexus.Domain.Text;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Contracts.Memory;

namespace BotNexus.Gateway.Search;

/// <summary>
/// Adapts the canonical per-agent memory query service to unified search without introducing a
/// second index or ranker.
/// </summary>
public sealed class MemorySearchContributor(
    IAgentRegistry agentRegistry,
    IAgentMemoryFactory memoryFactory) : ISearchContributor
{
    internal const int MaxSnippetLength = 240;

    /// <inheritdoc />
    public string SourceId => "memory";

    /// <inheritdoc />
    public string Label => "Memory";

    /// <inheritdoc />
    public bool IsAvailable => MemoryAgents().Count > 0;

    /// <inheritdoc />
    public bool CanAssessProvenanceTrust => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.Query) || request.MaxResults <= 0)
        {
            return [];
        }

        var remaining = request.MaxResults;
        var results = new List<SearchResult>(remaining);
        foreach (var descriptor in MemoryAgents())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var agentId = descriptor.AgentId.Value;
            var memory = memoryFactory.Create(agentId);
            var entries = await memory.SearchAsync(
                new AgentMemorySearchRequest(agentId, request.Query, remaining),
                cancellationToken).ConfigureAwait(false);

            foreach (var entry in entries.Take(remaining))
            {
                results.Add(new SearchResult(
                    Title: BuildTitle(descriptor.DisplayName, entry),
                    Snippet: TextTruncation.SafeTruncate(entry.Content, MaxSnippetLength, "...") ?? string.Empty,
                    Target: $"/memory/{Uri.EscapeDataString(agentId)}/entries/{Uri.EscapeDataString(entry.Id)}",
                    Timestamp: entry.CreatedAt,
                    Relevance: entry.RelevanceScore,
                    ProvenanceTrust: IsFirstParty(entry.TrustTier)
                        ? SearchProvenanceTrust.Trusted
                        : SearchProvenanceTrust.Untrusted));
            }

            remaining = request.MaxResults - results.Count;
            if (remaining == 0)
            {
                break;
            }
        }

        return results;
    }

    private IReadOnlyList<BotNexus.Gateway.Abstractions.Models.AgentDescriptor> MemoryAgents()
        => agentRegistry.GetAll()
            .Where(descriptor => descriptor.Memory is { Enabled: true })
            .OrderBy(descriptor => descriptor.AgentId.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string BuildTitle(
        string displayName,
        AgentMemorySearchResult entry)
        => $"{displayName}: {entry.SourceType}";

    private static bool IsFirstParty(string? trustTier)
        => string.Equals(trustTier, "trusted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trustTier, "derived", StringComparison.OrdinalIgnoreCase);
}
