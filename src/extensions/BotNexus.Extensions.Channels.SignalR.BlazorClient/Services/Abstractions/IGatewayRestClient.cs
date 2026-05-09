namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// All portal REST traffic. Nothing else.
/// </summary>
public interface IGatewayRestClient
{
    /// <summary>Set the API base URL derived from the hub URL.</summary>
    void Configure(string apiBaseUrl);

    /// <summary>GET /api/agents</summary>
    Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken = default);

    /// <summary>GET /api/conversations?agentId={agentId}</summary>
    Task<IReadOnlyList<ConversationSummaryDto>> GetConversationsAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/conversations/{conversationId}/history?limit={limit}&amp;offset={offset}</summary>
    Task<ConversationHistoryResponseDto?> GetHistoryAsync(
        string conversationId,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/conversations/{conversationId}</summary>
    Task<ConversationResponseDto?> GetConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/sessions?agentId={agentId}</summary>
    Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(
        string? agentId = null,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/sessions/{sessionId}/history?limit={limit}&amp;offset={offset}</summary>
    Task<SessionHistoryResponseDto?> GetSessionHistoryAsync(
        string sessionId,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>POST /api/conversations</summary>
    Task<ConversationResponseDto?> CreateConversationAsync(
        CreateConversationRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>PATCH /api/conversations/{conversationId}</summary>
    Task RenameConversationAsync(
        string conversationId,
        string newTitle,
        CancellationToken cancellationToken = default);

    /// <summary>DELETE /api/conversations/{conversationId} — soft delete (archive).</summary>
    Task<bool> ArchiveConversationAsync(string conversationId, CancellationToken ct = default);

    /// <summary>Current API base URL (set via Configure). Null if not yet configured.</summary>
    string? ApiBaseUrl { get; }
}
