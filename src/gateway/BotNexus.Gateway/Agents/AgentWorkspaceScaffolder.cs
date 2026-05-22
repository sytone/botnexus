using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Configuration;
using System.IO.Abstractions;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// Scaffolds the standard workspace directory structure and bootstrap files for a new agent
/// by delegating to <see cref="BotNexusHome.GetAgentDirectory"/>, which provisions the workspace
/// from embedded assembly templates on first access.
/// </summary>
public sealed class AgentWorkspaceScaffolder : IAgentWorkspaceScaffolder
{
    private readonly BotNexusHome _botNexusHome;

    /// <summary>
    /// Initializes a new instance of <see cref="AgentWorkspaceScaffolder"/>.
    /// </summary>
    public AgentWorkspaceScaffolder(BotNexusHome botNexusHome)
    {
        _botNexusHome = botNexusHome;
    }

    /// <inheritdoc/>
    public Task<string> ScaffoldAsync(string agentId, string displayName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        // Calling GetAgentDirectory triggers eager workspace provisioning (embedded templates)
        // when the directory does not yet exist, or a workspace migration for legacy layouts.
        var agentDirectory = _botNexusHome.GetAgentDirectory(agentId.Trim());
        var workspacePath = Path.Combine(agentDirectory, "workspace");
        return Task.FromResult(workspacePath);
    }
}
