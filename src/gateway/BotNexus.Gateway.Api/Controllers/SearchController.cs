using BotNexus.Gateway.Search;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Controllers;

/// <summary>
/// Provides bounded grouped search across uniformly registered contributors.
/// </summary>
[ApiController]
[Route("api/search")]
public sealed class SearchController(SearchAggregator aggregator) : ControllerBase
{
    /// <summary>Gets the largest per-source result limit accepted by the public endpoint.</summary>
    public const int MaximumLimit = 100;

    /// <summary>
    /// Searches all contributors, or a comma-separated source selection, without cross-source ranking.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AggregatedSearchGroup>>> Get(
        [FromQuery] string? query,
        [FromQuery] string? source,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, MaximumLimit);
        var sourceIds = ParseSourceIds(source);
        var groups = await aggregator.SearchAsync(
            query ?? string.Empty,
            boundedLimit,
            sourceIds,
            cancellationToken).ConfigureAwait(false);
        return Ok(groups);
    }

    private static IReadOnlySet<string>? ParseSourceIds(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        return source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
