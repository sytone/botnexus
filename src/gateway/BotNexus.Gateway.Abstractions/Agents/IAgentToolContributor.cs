using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Contributes runtime tools for a specific agent session during handle creation.
/// This allows extensions to add per-agent tools without compile-time Gateway references.
/// </summary>
public interface IAgentToolContributor
{
    /// <summary>
    /// Builds tools and optional lifetime resources for a specific agent/session context.
    /// </summary>
    Task<AgentToolContribution> ContributeAsync(
        AgentToolContributionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Session-scoped context passed to extension contributors when building agent tools.
/// </summary>
/// <param name="Descriptor">Agent descriptor being materialized into a runtime handle.</param>
/// <param name="ExecutionContext">Execution context containing session metadata/history.</param>
/// <param name="WorkspacePath">Resolved workspace directory for the agent.</param>
/// <param name="PathValidator">Path policy validator for workspace-safe file access.</param>
/// <param name="CopilotMcpEndpoint">
/// The fully resolved GitHub Copilot MCP endpoint for this agent: the enterprise MCP host when an
/// endpoint override is configured for the provider, otherwise the individual/fallback host
/// (<c>https://api.githubcopilot.com/mcp</c>). Resolved once at the registration seam so extensions
/// consume a ready-to-use value instead of re-deriving it from a raw provider-endpoint override (#1797).
/// <c>null</c> when the agent's provider has no Copilot MCP endpoint.
/// </param>
/// <param name="GetProviderApiKeyAsync">Resolves an API key for a provider key.</param>
public sealed record AgentToolContributionContext(
    AgentDescriptor Descriptor,
    AgentExecutionContext ExecutionContext,
    string WorkspacePath,
    IPathValidator PathValidator,
    string? CopilotMcpEndpoint,
    Func<string, CancellationToken, Task<string?>> GetProviderApiKeyAsync);

/// <summary>
/// Result returned by an <see cref="IAgentToolContributor"/> containing tools and
/// optional session-scoped resources that should be disposed with the agent handle.
/// </summary>
/// <param name="Tools">Tools contributed for the target agent session.</param>
/// <param name="ResourcesToDispose">Additional resources to dispose with the handle lifecycle.</param>
public sealed record AgentToolContribution(
    IReadOnlyList<IAgentTool> Tools,
    IReadOnlyList<object>? ResourcesToDispose = null);
