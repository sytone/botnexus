using BotNexus.Agent.Core.Tools;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Registry for discovering and resolving agent tools.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this path is flat and global (decision recorded for #3539).</strong> This registry
/// collects <c>IAgentTool</c> singletons once at startup
/// (<c>DefaultToolRegistry(sp.GetServices&lt;IAgentTool&gt;())</c>) and hands the same instances to
/// every agent handle. Extensions, by contrast, contribute through
/// <c>IAgentToolContributor</c>, which is per-agent, asynchronous and lifetime-aware. That
/// asymmetry is deliberate, and the reason is recorded here so it is not mistaken for drift.
/// </para>
/// <para>
/// This is NOT the gateway's only built-in tool path, which is the detail that makes the asymmetry
/// acceptable. <c>IAgentToolFactory</c> is the per-agent built-in path and already receives the
/// workspace directory, path validator and shell command; <c>InProcessIsolationStrategy</c>
/// composes the factory tools, this registry's tools and the extension contributions into a single
/// list per handle, with the per-agent factory tools taking precedence on a name collision.
/// </para>
/// <para>
/// So this registry is for tools that are genuinely agent-invariant - their behaviour does not vary
/// by workspace, file-access policy or session. For those, a per-session factory would allocate a
/// new instance per agent to produce identical behaviour. A tool that DOES vary by agent belongs on
/// <c>IAgentToolFactory</c> (synchronous) or <c>IAgentToolContributor</c> (asynchronous, or needing
/// handle-scoped disposal) - not here. Adding an agent-varying tool to this flat path is the actual
/// defect this note exists to prevent.
/// </para>
/// </remarks>
public interface IToolRegistry
{
    /// <summary>
    /// Gets all registered tools.
    /// </summary>
    IReadOnlyList<IAgentTool> GetAll();

    /// <summary>
    /// Gets a tool by name.
    /// </summary>
    /// <param name="name">Tool name (case-insensitive).</param>
    /// <returns>The tool, or null if not found.</returns>
    IAgentTool? GetByName(string name);

    /// <summary>
    /// Resolves a list of tools by their identifiers.
    /// </summary>
    /// <param name="toolIds">Tool names to resolve.</param>
    /// <returns>List of resolved tools (missing tools are silently skipped).</returns>
    IReadOnlyList<IAgentTool> ResolveTools(IEnumerable<string> toolIds);
}
