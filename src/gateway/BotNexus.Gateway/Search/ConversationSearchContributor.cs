using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;

namespace BotNexus.Gateway.Search;

/// <summary>
/// Searches lightweight conversation summaries from the canonical conversation store.
/// </summary>
public sealed class ConversationSearchContributor(IConversationStore conversationStore) : ISearchContributor
{
    internal const int MaxSnippetLength = 240;

    /// <inheritdoc />
    public string SourceId => "conversations";

    /// <inheritdoc />
    public string Label => "Conversations";

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public bool CanAssessProvenanceTrust => false;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Query) || request.MaxResults <= 0)
            return [];

        var summaries = await conversationStore.ListAsync(null, cancellationToken).ConfigureAwait(false);
        var results = new List<SearchResult>(request.MaxResults);
        foreach (var summary in summaries.OrderByDescending(item => item.UpdatedAt)
                     .ThenBy(item => item.ConversationId.Value, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SearchContributorText.ContainsAny(request.Query, summary.ConversationId.Value, summary.AgentId.Value,
                    summary.Title, summary.Purpose, summary.Status.ToString()))
                continue;

            var snippet = summary.Purpose ?? $"{summary.AgentId} - {summary.Status}";
            results.Add(new SearchResult(
                Title: summary.Title,
                Snippet: SearchContributorText.BoundedSnippet(snippet, MaxSnippetLength),
                Target: $"/chat/{Uri.EscapeDataString(summary.AgentId.Value)}/{Uri.EscapeDataString(summary.ConversationId.Value)}",
                Timestamp: summary.UpdatedAt,
                ProvenanceTrust: SearchProvenanceTrust.Untrusted));
            if (results.Count == request.MaxResults)
                break;
        }

        return results;
    }
}
