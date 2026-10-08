using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Abstractions.Security;
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
    /// Searches core-reviewed contributors within the authenticated caller's scope, without cross-source ranking.
    /// Optional agent and agentId query aliases intersect that scope; ambiguous or denied selectors fail closed.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<AggregatedSearchGroup>>> Get(
        [FromQuery] string? query,
        [FromQuery] string? source,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (HttpContext?.Items[GatewayAuthHttpContext.CallerIdentityItemKey] is not GatewayCallerIdentity caller)
            return StatusCode(403, "Caller is not authorized for this search.");

        var scope = caller.IsAdmin || caller.AllowedAgents.Count == 0
            ? SearchScope.All
            : SearchScope.ForAgents(caller.AllowedAgents.Select(AgentId.From));
        string? selector = null;
        foreach (var name in new[] { "agent", "agentId" })
        {
            if (!Request.Query.TryGetValue(name, out var values))
                continue;
            if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
                return StatusCode(403, "Caller is not authorized for this search.");
            var value = values[0]?.Trim();
            if (selector is not null && !string.Equals(selector, value, StringComparison.OrdinalIgnoreCase))
                return StatusCode(403, "Caller is not authorized for this search.");
            selector = value;
        }
        if (selector is not null)
        {
            scope = scope.Intersect(AgentId.From(selector));
            if (scope.Agents.Count == 0)
                return StatusCode(403, "Caller is not authorized for this search.");
        }

        var boundedLimit = Math.Clamp(limit, 1, MaximumLimit);
        var sourceIds = ParseSourceIds(source);
        var groups = await aggregator.SearchAsync(
            query ?? string.Empty,
            boundedLimit,
            scope,
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
