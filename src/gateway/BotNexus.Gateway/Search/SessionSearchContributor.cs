using System.Collections.Frozen;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Search;

/// <summary>
/// Searches a bounded metadata-only window of session summaries without loading transcripts.
/// </summary>
public sealed class SessionSearchContributor(ISessionStore sessionStore, IConversationStore conversationStore,
    IAgentRegistry agentRegistry) : ISearchContributor
{
    internal const int MaxSnippetLength = 240;
    internal const int MaxScannedSummaries = 500;

    /// <inheritdoc />
    public string SourceId => "sessions";

    /// <inheritdoc />
    public string Label => "Sessions";

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

        var conversations = await SearchableConversations.ListAsync(conversationStore, request.Scope, cancellationToken, agentRegistry).ConfigureAwait(false);
        var eligible = conversations.Select(conversation => conversation.ConversationId).ToFrozenSet();
        if (eligible.Count == 0)
            return [];
        var agentFilter = !request.Scope.IsAll && request.Scope.Agents.Count == 1
            ? conversations[0].AgentId.Value : null;
        var page = await sessionStore.ListSummaryPageAsync(
            new SessionSummaryQuery(DateTimeOffset.MinValue, AgentId: agentFilter,
                IncludeInactive: true, Limit: MaxScannedSummaries, ConversationIds: eligible),
            cancellationToken).ConfigureAwait(false);
        var results = new List<SearchResult>(request.MaxResults);
        foreach (var summary in page.Items.OrderByDescending(item => item.UpdatedAt)
                     .ThenBy(item => item.SessionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.Scope.Allows(AgentId.From(summary.AgentId))
                || string.IsNullOrWhiteSpace(summary.ConversationId)
                || !eligible.Contains(ConversationId.From(summary.ConversationId)))
                continue;
            if (!SearchContributorText.ContainsAny(request.Query, summary.SessionId, summary.AgentId,
                    summary.ConversationId, summary.ChannelType?.Value, summary.Status.ToString(), summary.SessionType.Value))
                continue;

            var snippet = $"{summary.AgentId} - {summary.Status} - {summary.MessageCount} messages";
            var target = string.IsNullOrWhiteSpace(summary.ConversationId)
                ? $"/chat/{Uri.EscapeDataString(summary.AgentId)}"
                : $"/chat/{Uri.EscapeDataString(summary.AgentId)}/{Uri.EscapeDataString(summary.ConversationId)}";
            results.Add(new SearchResult(
                Title: summary.SessionId,
                Snippet: SearchContributorText.BoundedSnippet(snippet, MaxSnippetLength),
                Target: target,
                Timestamp: summary.UpdatedAt,
                ProvenanceTrust: SearchProvenanceTrust.Untrusted));
            if (results.Count == request.MaxResults)
                break;
        }

        return results;
    }
}
