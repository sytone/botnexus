namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Thin action API for components that need to send messages or mutate agent sessions.
/// Does not own state — delegates to <see cref="GatewayHubConnection"/> and
/// <see cref="IGatewayRestClient"/> and surfaces results via <see cref="IClientStateStore"/>.
/// </summary>
public interface IAgentInteractionService
{
    Task SendMessageAsync(string agentId, string content);
    Task SteerAsync(string agentId, string content);
    Task FollowUpAsync(string agentId, string content);
    Task AbortAsync(string agentId);
    Task ResetSessionAsync(string agentId);
    Task<CompactSessionResult?> CompactSessionAsync(string agentId);
    Task<string?> CreateConversationAsync(string agentId, string? title = null, bool select = true);
    Task SelectConversationAsync(string agentId, string conversationId);
    Task RenameConversationAsync(string agentId, string? conversationId, string newTitle);
    Task ArchiveConversationAsync(string agentId, string conversationId);
    Task RefreshAgentsAsync();
    Task ViewSubAgentAsync(SubAgentInfo subAgent);
    void ClearLocalMessages(string agentId);
}
