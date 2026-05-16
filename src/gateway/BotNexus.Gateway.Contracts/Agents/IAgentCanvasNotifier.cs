namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Broadcasts agent-scoped canvas HTML updates to interested transports.
/// </summary>
public interface IAgentCanvasNotifier
{
    /// <summary>
    /// Publishes the latest canvas HTML for a specific agent.
    /// </summary>
    Task NotifyCanvasUpdatedAsync(string agentId, string html, CancellationToken cancellationToken = default);
}
