using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Agent.Core.Tools;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Contributes runtime tools for a specific agent session during handle creation.
/// This allows extensions to add per-agent tools without compile-time Gateway references.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why no built-in implements this contract (decision recorded for #3539).</strong> The
/// standing platform direction is that built-in capabilities should use the same contracts as
/// extensions, so the extension path cannot rot. This interface is a deliberate, reviewed
/// exception, and the reason is recorded here so a future scan reads it instead of re-deriving
/// whether the asymmetry was intentional.
/// </para>
/// <para>
/// The audit's original premise was that built-in tools are global and static and therefore cannot
/// reach per-agent context. That is true of only one of the two built-in paths. The gateway has
/// THREE tool paths, and <c>InProcessIsolationStrategy</c> composes all three into one list per
/// handle:
/// </para>
/// <list type="number">
///   <item><description>
///   <c>IAgentToolFactory.CreateTools</c> - the per-agent built-in path. It already receives the
///   resolved workspace directory, the agent's <c>IPathValidator</c> and the agent's shell command,
///   and constructs fresh tool instances per handle. That is the same per-agent context an
///   <see cref="AgentToolContributionContext"/> carries, so the file, shell and search tools are
///   not missing anything by not implementing this interface.
///   </description></item>
///   <item><description>
///   <c>IToolRegistry</c> - the flat, global path for tools that are genuinely agent-invariant.
///   See the rationale on <c>IToolRegistry</c> for why that path stays flat.
///   </description></item>
///   <item><description>
///   This interface - the extension path. Its distinguishing capability over
///   <c>IAgentToolFactory</c> is not per-agent context but ASYNCHRONY (<c>ContributeAsync</c>,
///   needed to start out-of-process backends such as MCP),
///   <see cref="AgentToolContribution.ResourcesToDispose"/> tied to the handle lifecycle, and the
///   ability to build tools with no compile-time reference to the Gateway assembly.
///   </description></item>
/// </list>
/// <para>
/// A built-in has, by definition, a compile-time Gateway reference, so the third capability is
/// worth nothing to it, and a built-in needing per-agent context already has it via
/// <c>IAgentToolFactory</c>. Routing built-ins through this contract would add an async per-session
/// allocation for no reachable benefit. <strong>If a built-in ever needs asynchronous construction
/// or handle-scoped disposal, this is the contract it should move to</strong> - that migration is
/// in scope and needs no new decision; it is the recorded intent.
/// </para>
/// <para>
/// The rot risk the direction warns about is real, and is mitigated structurally rather than by
/// convention: <c>ContributorBuiltInParticipationFenceArchitectureTests</c> fails the build when a
/// newly added contributor interface has neither a built-in implementation nor a written exemption,
/// so this scan never has to be run by hand again.
/// </para>
/// </remarks>
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
