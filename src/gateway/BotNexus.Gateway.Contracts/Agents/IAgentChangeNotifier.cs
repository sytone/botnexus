namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Publishes agent catalog change notifications to external clients.
/// Implementations bridge gateway-level events to transport-specific channels.
/// </summary>
public interface IAgentChangeNotifier
{
    /// <summary>
    /// Notifies channel clients that the set of configured agents has changed.
    /// </summary>
    /// <param name="changeType">Change classification (for example: added, updated, removed).</param>
    /// <param name="agentId">Affected agent identifier when available.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task NotifyAgentsChangedAsync(string changeType, string? agentId, CancellationToken cancellationToken = default);
}
