using System.Collections.Frozen;
using System.Collections.Immutable;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Extensions;

/// <summary>Immutable server-derived agent authorization for a search, distinct from source selection.</summary>
public sealed class SearchScope
{
    private readonly FrozenSet<string> _agentIds;

    private SearchScope(bool isAll, IEnumerable<AgentId> agents)
    {
        IsAll = isAll;
        _agentIds = agents.Select(agent => agent.Value).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        Agents = _agentIds.Order(StringComparer.Ordinal).Select(AgentId.From).ToImmutableArray();
    }

    /// <summary>Gets unrestricted agent access. This does not authorize unreviewed contributors.</summary>
    public static SearchScope All { get; } = new(true, []);

    /// <summary>Gets whether all agent partitions are authorized.</summary>
    public bool IsAll { get; }

    /// <summary>Gets the explicit authorized partitions; empty with <see cref="IsAll"/> false means none.</summary>
    public IReadOnlyList<AgentId> Agents { get; }

    /// <summary>Copies an explicit case-insensitive agent set. An empty set authorizes no agents.</summary>
    public static SearchScope ForAgents(IEnumerable<AgentId> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return new(false, agents);
    }

    /// <summary>Tests an agent before any backing read, matching, snippet generation, or result bound.</summary>
    public bool Allows(AgentId agentId) => IsAll || _agentIds.Contains(agentId.Value);

    /// <summary>Intersects a client selector with this server-derived scope without widening it.</summary>
    public SearchScope Intersect(AgentId agentId) => ForAgents(Allows(agentId) ? [agentId] : []);
}
