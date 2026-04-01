using BotNexus.Core.Abstractions;

namespace BotNexus.Agent;

public sealed class AgentWorkspaceFactory : IAgentWorkspaceFactory
{
    public IAgentWorkspace Create(string agentName) => new AgentWorkspace(agentName);
}
