namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Scaffolds the standard workspace directory structure and bootstrap files for a new agent.
/// </summary>
public interface IAgentWorkspaceScaffolder
{
    /// <summary>
    /// Creates the agent workspace directory and writes scaffold files if they do not already exist.
    /// </summary>
    /// <param name="agentId">The agent identifier (used for directory naming).</param>
    /// <param name="displayName">Human-readable display name used to populate scaffold placeholders.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The absolute path of the scaffolded workspace directory.</returns>
    Task<string> ScaffoldAsync(string agentId, string displayName, CancellationToken cancellationToken = default);
}
