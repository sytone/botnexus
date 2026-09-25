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
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResultsPerSource);
        cancellationToken.ThrowIfCancellationRequested();

        var selected = _contributors
            .Where(contributor => sourceIds is null || sourceIds.Contains(contributor.SourceId))
            .ToArray();
        var tasks = selected
            .Select(contributor => SearchSourceAsync(contributor, query, maxResultsPerSource, cancellationToken))
            .ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<AggregatedSearchGroup> SearchSourceAsync(
        ISearchContributor contributor,
        string query,
        int maxResults,
        CancellationToken callerCancellation)
    {
        if (!contributor.IsAvailable)
            return Group(contributor, false, [], null);

        var timeout = ResolveTimeout(contributor.SourceId);
        using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        sourceCancellation.CancelAfter(timeout);

        try
        {
            var request = new SearchRequest(query, maxResults);
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
