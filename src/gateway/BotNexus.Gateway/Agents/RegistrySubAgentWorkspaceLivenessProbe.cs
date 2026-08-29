using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Answers sub-agent workspace liveness from the agent registry - the component that actually knows
/// (issue #3569).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the registry is the right authority.</b> <c>DefaultSubAgentManager</c> registers the child
/// agent descriptor as part of spawning a sub-agent, and unregisters it in the terminal cleanup path
/// that every disposition (completed, failed, killed, timed out, budget exhausted) routes through.
/// Registration is therefore a lifecycle-driven fact with no clock in it: the descriptor exists for
/// exactly as long as the run does. That is precisely the signal the age-based sweep was missing
/// when it deleted 37 live sub-agents' workspaces in a single week.
/// </para>
/// <para>
/// <b>The directory name IS the child agent id.</b> <c>FileAgentWorkspaceManager.GetWorkspacePath</c>
/// builds the workspace path from the sanitized child agent id, so the on-disk directory name maps
/// back to the registry key directly. A name that cannot be parsed as an <see cref="AgentId"/> is
/// treated as live rather than guessed at.
/// </para>
/// <para>
/// <b>Fail-safe direction.</b> Every uncertain outcome - unparsable name, registry throwing - answers
/// "live". Keeping a dead workspace for one more sweep interval costs disk space; deleting a live one
/// destroys the entire run and hands the parent a confident but wrong summary.
/// </para>
/// </remarks>
public sealed class RegistrySubAgentWorkspaceLivenessProbe(
    IAgentRegistry registry,
    ILogger<RegistrySubAgentWorkspaceLivenessProbe> logger) : ISubAgentWorkspaceLivenessProbe
{
    private readonly IAgentRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly ILogger<RegistrySubAgentWorkspaceLivenessProbe> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public bool IsLive(string workspaceDirectoryName)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectoryName))
            return true;

        AgentId agentId;
        try
        {
            agentId = AgentId.From(workspaceDirectoryName);
        }
        catch (Exception ex)
        {
            // Cannot identify the owner, so cannot prove it is dead. Retain.
            _logger.LogDebug(
                ex,
                "Could not resolve an agent id from sub-agent workspace directory '{Directory}'; treating it as live and retaining it.",
                workspaceDirectoryName);
            return true;
        }

        try
        {
            return _registry.Contains(agentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Agent registry lookup failed for sub-agent workspace '{Directory}'; treating it as live and retaining it.",
                workspaceDirectoryName);
            return true;
        }
    }
}
