namespace BotNexus.Gateway.Abstractions.Agents;
/// <summary>
/// Broadcasts canvas HTML updates to interested transports, scoped to agent and conversation.
/// </summary>
public interface IAgentCanvasNotifier
{
    /// <summary>
    /// Publishes the latest canvas HTML for a specific agent and conversation.
    /// </summary>
    Task NotifyCanvasUpdatedAsync(string agentId, string conversationId, string html, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a canvas state key change for a specific conversation.
    /// A null value indicates the key was deleted.
    /// </summary>
    Task NotifyCanvasStateChangedAsync(string conversationId, string key, object? value, CancellationToken cancellationToken = default);
}
