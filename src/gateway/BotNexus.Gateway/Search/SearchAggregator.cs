using BotNexus.Gateway.Abstractions.Extensions;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Search;

/// <summary>
/// Fans a bounded query out across all registered search contributors without imposing global ranking.
/// </summary>
public sealed class SearchAggregator
{
    private readonly IReadOnlyList<ISearchContributor> _contributors;
    private readonly SearchAggregationOptions _options;

    /// <summary>
    /// Creates an aggregator over the complete DI collection so built-in and extension sources use one path.
    /// </summary>
    public SearchAggregator(
        IEnumerable<ISearchContributor> contributors,
        IOptions<SearchAggregationOptions> options)
    {
        _contributors = contributors.ToArray();
        _options = options.Value;
    }

    /// <summary>
    /// Searches selected contributors concurrently and returns one outcome group per selected source.
    /// </summary>
    /// <param name="query">Query text; blank text performs no contributor calls.</param>
    /// <param name="maxResultsPerSource">Maximum results retained from each contributor.</param>
    /// <param name="sourceIds">Optional case-insensitive source selection.</param>
    /// <param name="cancellationToken">Propagates caller cancellation across all source work.</param>
    public async Task<IReadOnlyList<AggregatedSearchGroup>> SearchAsync(
        string query,
        int maxResultsPerSource,
        IReadOnlySet<string>? sourceIds = null,
        CancellationToken cancellationToken = default)
        => await SearchCoreAsync(query, maxResultsPerSource, null, sourceIds, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Searches only core-reviewed exact contributor types with explicit authorization, including for all-agent callers.
    /// Unreviewed extensions are excluded before any contributor property or method is accessed.
    /// </summary>
    public Task<IReadOnlyList<AggregatedSearchGroup>> SearchAsync(
        string query,
        int maxResultsPerSource,
        SearchScope scope,
        IReadOnlySet<string>? sourceIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return SearchCoreAsync(query, maxResultsPerSource, scope, sourceIds, cancellationToken);
    }

    private async Task<IReadOnlyList<AggregatedSearchGroup>> SearchCoreAsync(
        string query, int maxResultsPerSource, SearchScope? scope,
        IReadOnlySet<string>? sourceIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResultsPerSource);
        cancellationToken.ThrowIfCancellationRequested();

        if (scope is { IsAll: false, Agents.Count: 0 })
            return [];

        var selected = _contributors
            .Where(contributor => scope is null || IsReviewed(contributor.GetType()))
            .Where(contributor => sourceIds is null || sourceIds.Contains(contributor.SourceId))
            .ToArray();
        var tasks = selected
            .Select(contributor => SearchSourceAsync(contributor, query, maxResultsPerSource, scope, cancellationToken))
            .ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<AggregatedSearchGroup> SearchSourceAsync(
        ISearchContributor contributor,
        string query,
        int maxResults,
        SearchScope? scope,
        CancellationToken callerCancellation)
    {
        // Scoped availability is core-owned and never probes unauthorized workspaces.
        var available = scope is null ? contributor.IsAvailable : contributor switch
        {
            AgentSearchContributor agents => agents.IsAvailableFor(scope),
            MemorySearchContributor memory => memory.IsAvailableFor(scope),
            FileSearchContributor files => files.IsAvailableFor(scope),
            _ => true
        };
        if (!available)
            return Group(contributor, false, [], null);

        var timeout = ResolveTimeout(contributor.SourceId);
        using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        sourceCancellation.CancelAfter(timeout);

        try
        {
            var request = new SearchRequest(query, maxResults, scope ?? SearchScope.All);
            var results = await contributor.SearchAsync(request, sourceCancellation.Token)
                .WaitAsync(sourceCancellation.Token)
                .ConfigureAwait(false);
            callerCancellation.ThrowIfCancellationRequested();
            var bounded = results.Take(maxResults).ToArray();
            return Group(contributor, true, bounded, null);
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (sourceCancellation.IsCancellationRequested)
        {
            return Group(contributor, true, [], "Source search timed out.");
        }
        catch (Exception)
        {
            return Group(contributor, true, [], "Source search failed.");
        }
    }

    private static bool IsReviewed(Type type)
        => type == typeof(AgentSearchContributor)
            || type == typeof(ConversationSearchContributor)
            || type == typeof(SessionSearchContributor)
            || type == typeof(MemorySearchContributor)
            || type == typeof(FileSearchContributor);

    private TimeSpan ResolveTimeout(string sourceId)
    {
        var timeout = _options.SourceTimeouts.TryGetValue(sourceId, out var sourceTimeout)
            ? sourceTimeout
            : _options.DefaultSourceTimeout;
        return timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(5);
    }

    private static AggregatedSearchGroup Group(
        ISearchContributor contributor,
        bool isAvailable,
        IReadOnlyList<SearchResult> results,
        string? error)
        => new(
            contributor.SourceId,
            contributor.Label,
            isAvailable,
            contributor.CanAssessProvenanceTrust,
            results.Count,
            results,
            error);
}
