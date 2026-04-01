using BotNexus.Core.Models;

namespace BotNexus.Core.Abstractions;

/// <summary>Contract for running an agent against an inbound message.</summary>
public interface IAgentRunner
{
    /// <summary>Unique agent name handled by this runner.</summary>
    string AgentName { get; }

    /// <summary>Processes an inbound message through the agent pipeline.</summary>
    Task RunAsync(InboundMessage message, CancellationToken cancellationToken = default);
}
