namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Thrown when an agent cannot start another session because it reached its concurrency limit.
/// </summary>
public sealed class AgentConcurrencyLimitExceededException : Exception
{
    public AgentConcurrencyLimitExceededException(string agentId, int maxConcurrentSessions)
        : base($"Agent '{agentId}' has reached MaxConcurrentSessions ({maxConcurrentSessions}).")
    {
        AgentId = agentId;
        MaxConcurrentSessions = maxConcurrentSessions;
    }

    public string AgentId { get; }

    public int MaxConcurrentSessions { get; }
}
