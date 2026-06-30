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
    Task InterruptAndSteerAsync(string agentId, string message);
    Task ResetSessionAsync(string agentId);
    Task<CompactSessionResult?> CompactSessionAsync(string agentId);
    Task<string?> CreateConversationAsync(string agentId, string? title = null, bool select = true);
    Task SelectConversationAsync(string agentId, string conversationId);

    /// <summary>
    /// Fetches the next older page of history for the conversation (offset advances by
    /// <see cref="AgentInteractionService.DefaultHistoryPageSize"/>), prepends it, and stops
    /// when a page returns fewer rows than the page size. Shared by desktop and mobile so the
    /// scroll-up load-more behaves identically. Returns the number of rows prepended (0 when
    /// nothing more is available or a fetch is already in flight).
    /// </summary>
    Task<int> LoadMoreHistoryAsync(string agentId, string conversationId);
    Task RenameConversationAsync(string agentId, string? conversationId, string newTitle);
    Task ArchiveConversationAsync(string agentId, string conversationId);
    Task RefreshAgentsAsync();
    Task RefreshConversationsAsync(string agentId);
    Task ViewSubAgentAsync(SubAgentInfo subAgent);
    Task RespondToAskUserAsync(string conversationId, string requestId, string? freeFormText, string[]? selectedValues, bool cancelled);
    void ClearLocalMessages(string agentId);
}
