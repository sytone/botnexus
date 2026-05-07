namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Manages file-based workspace content for an agent.
/// </summary>
public interface IAgentWorkspaceManager
{
    /// <summary>
    /// Loads workspace files for the specified agent.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current workspace snapshot.</returns>
    Task<AgentWorkspace> LoadWorkspaceAsync(string agentName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a memory entry to the agent workspace.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <param name="content">The content to append to memory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveMemoryAsync(string agentName, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a memory entry to a specific markdown file under the agent memory root.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <param name="filePath">Relative path within memory root, or null for today's daily note.</param>
    /// <param name="content">The content to append.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveMemoryAsync(string agentName, string? filePath, string content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the absolute workspace path for an agent.
    /// </summary>
    /// <param name="agentName">The agent identifier.</param>
    /// <returns>The workspace directory path.</returns>
    string GetWorkspacePath(string agentName);
}
