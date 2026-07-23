using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Built-in sub-agent archetype catalog. Replaces the previous <c>BuiltInAgents</c> named-agent
/// registration (#2136): the six worker archetypes are no longer modelled as ordinary named agents
/// with empty model/provider and leaked into the global <see cref="Abstractions.Agents.IAgentRegistry"/>.
/// Instead they exist purely as spawn-time profiles resolved by
/// <see cref="DefaultSubAgentManager.ResolveSpawnPlan"/>, which clones the <i>parent</i> descriptor
/// (inheriting its model/provider) and applies the archetype's tool restriction.
/// </summary>
/// <remarks>
/// These ids are reserved: they can never be created, updated, or targeted as real conversational
/// agents (<c>agent_converse</c> / <c>spawn_subagent(targetAgentId:...)</c>). A sub-agent role is an
/// implementation detail, not a peer identity - advertising it as an <c>agent_converse</c> target
/// previously failed with "ModelId is required; ApiProvider is required" and surfaced as fatal
/// UnobservedTaskException breadcrumbs.
/// </remarks>
public static class BuiltInArchetypes
{
    /// <summary>
    /// A built-in archetype profile: the tool restriction and role description applied to a spawned
    /// sub-agent when the caller selects this archetype. The system prompt/model/provider are still
    /// inherited from the spawning (parent) agent - only the tool set is narrowed to the role.
    /// </summary>
    /// <param name="ToolIds">The tool allowlist the archetype restricts the sub-agent to.</param>
    /// <param name="Description">Human-readable role description (portal/list only).</param>
    public sealed record ArchetypeProfile(IReadOnlyList<string> ToolIds, string Description);

    /// <summary>
    /// The reserved archetype/worker-role ids that must never be registered as named conversational
    /// agents, created, updated, or targeted via <c>agent_converse</c> /
    /// <c>spawn_subagent(targetAgentId:...)</c>. Matches the historical <c>BuiltInAgents</c> set so
    /// stale config or stale conversations referencing them are rejected deterministically (#2136).
    /// </summary>
    public static IReadOnlySet<string> ReservedAgentIds { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "researcher",
            "coder",
            "planner",
            "reviewer",
            "writer",
            "analyst",
        };

    private static readonly IReadOnlyDictionary<string, ArchetypeProfile> Profiles =
        new Dictionary<string, ArchetypeProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["researcher"] = new(
                ["web_search", "web_fetch", "memory_search", "memory_get", "read", "glob", "grep"],
                "Web search, URL fetch, and summarization. Read-only - no code execution."),
            ["coder"] = new(
                ["read", "write", "edit", "glob", "grep", "shell", "exec", "process", "watch_file"],
                "Code writing, editing, building, and testing. Full file and shell access."),
            ["planner"] = new(
                ["memory_search", "memory_save", "memory_get", "web_search", "read", "write"],
                "Issue decomposition, spec writing, and task breakdown. Memory and web access."),
            ["reviewer"] = new(
                ["read", "glob", "grep", "shell", "web_fetch", "memory_search"],
                "Code review, PR analysis, and quality checks. Read-only file and shell access."),
            ["writer"] = new(
                ["read", "write", "edit", "glob", "grep", "web_search", "web_fetch", "memory_search"],
                "Documentation, changelogs, summaries, and content creation. File write access."),
            ["analyst"] = new(
                ["read", "glob", "grep", "shell", "exec", "web_fetch"],
                "Data analysis, log triage, and metrics. Read and shell access."),
        };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="id"/> is a reserved archetype/worker-role
    /// id that may not be used as a real conversational agent id.
    /// </summary>
    public static bool IsReserved(string? id)
        => !string.IsNullOrWhiteSpace(id) && ReservedAgentIds.Contains(id.Trim());

    /// <summary>
    /// Resolves the archetype profile for <paramref name="archetype"/>, or <see langword="null"/>
    /// when the archetype has no built-in tool restriction (e.g. <c>general</c>), in which case the
    /// spawned sub-agent inherits the parent's tool set unchanged.
    /// </summary>
    public static ArchetypeProfile? GetProfile(SubAgentArchetype archetype)
        => Profiles.TryGetValue(archetype.Value, out var profile) ? profile : null;
}
