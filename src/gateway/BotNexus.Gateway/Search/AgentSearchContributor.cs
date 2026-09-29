using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Search;

/// <summary>
/// Searches registered agent descriptors without maintaining a parallel index.
/// </summary>
public sealed class AgentSearchContributor(IAgentRegistry agentRegistry) : ISearchContributor
{
    internal const int MaxSnippetLength = 240;

    /// <inheritdoc />
    public string SourceId => "agents";

    /// <inheritdoc />
    public string Label => "Agents";

    /// <inheritdoc />
    public bool IsAvailable => agentRegistry.GetAll().Count > 0;

    /// <inheritdoc />
    public bool CanAssessProvenanceTrust => false;

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Query) || request.MaxResults <= 0)
            return Task.FromResult<IReadOnlyList<SearchResult>>([]);

        var results = new List<SearchResult>(request.MaxResults);
        foreach (var descriptor in agentRegistry.GetAll()
                     .OrderBy(agent => agent.AgentId.Value, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(agent => agent.AgentId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Matches(descriptor, request.Query))
                continue;

            results.Add(new SearchResult(
                Title: descriptor.DisplayName,
                Snippet: BuildSnippet(descriptor),
                Target: $"/agents/{Uri.EscapeDataString(descriptor.AgentId.Value)}",
                Timestamp: DateTimeOffset.MinValue,
                ProvenanceTrust: SearchProvenanceTrust.Untrusted));
            if (results.Count == request.MaxResults)
                break;
        }

        return Task.FromResult<IReadOnlyList<SearchResult>>(results);
    }

    private static bool Matches(AgentDescriptor descriptor, string query)
        => SearchContributorText.ContainsAny(query, descriptor.AgentId.Value, descriptor.DisplayName,
            descriptor.Description, descriptor.Summary, descriptor.ModelId, descriptor.ApiProvider);

    private static string BuildSnippet(AgentDescriptor descriptor)
    {
        var text = descriptor.Summary ?? descriptor.Description
            ?? $"{descriptor.ApiProvider} - {descriptor.ModelId}";
        return SearchContributorText.BoundedSnippet(text, MaxSnippetLength);
    }
}
