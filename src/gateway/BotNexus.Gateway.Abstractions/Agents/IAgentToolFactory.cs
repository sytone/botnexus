using BotNexus.AgentCore.Tools;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Creates built-in workspace-scoped tools for an agent.
/// </summary>
public interface IAgentToolFactory
{
    /// <summary>
    /// Creates tools scoped to the provided working directory.
    /// </summary>
    /// <param name="workingDirectory">Agent workspace root path.</param>
    /// <returns>Built-in tools bound to the workspace.</returns>
    IReadOnlyList<IAgentTool> CreateTools(string workingDirectory);
}
