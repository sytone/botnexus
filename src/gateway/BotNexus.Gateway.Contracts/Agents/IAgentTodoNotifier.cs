namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Broadcasts per-conversation todo state updates to interested transports, scoped to agent
/// and conversation. Mirrors <see cref="IAgentCanvasNotifier"/> for the todo primitive (#1464).
/// </summary>
/// <remarks>
/// Unlike canvas, the todo state is already persisted on the conversation row (<c>TodoJson</c>) by
/// the <c>todo</c> tool itself, so this notifier only needs to push the change out for live
/// portal updates — it does not persist. The SignalR channel provides the broadcasting
/// implementation, auto-discovered by the extension loader via the contract allowlist.
/// </remarks>
public interface IAgentTodoNotifier
{
    /// <summary>
    /// Publishes the latest todo state (the raw <c>TodoJson</c> payload, or <c>null</c>/empty when the
    /// list was cleared) for a specific agent and conversation so live clients can refresh.
    /// </summary>
    Task NotifyTodoUpdatedAsync(string agentId, string conversationId, string? todoJson, CancellationToken cancellationToken = default);
}
