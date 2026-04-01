namespace BotNexus.Core.Abstractions;

/// <summary>
/// Creates agent workspace instances for a specific agent.
/// </summary>
public interface IAgentWorkspaceFactory
{
    /// <summary>
    /// Creates a workspace bound to the supplied agent name.
    /// </summary>
    IAgentWorkspace Create(string agentName);
}
