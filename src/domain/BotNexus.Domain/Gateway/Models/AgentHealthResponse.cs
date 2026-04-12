using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Health check response for an agent and its active runtime instances.
/// </summary>
/// <param name="Status">Health status: healthy, unhealthy, or unknown.</param>
/// <param name="AgentId">The agent identifier.</param>
/// <param name="InstanceCount">Number of active instances for the agent.</param>
public sealed record AgentHealthResponse(string Status, AgentId AgentId, int InstanceCount);
