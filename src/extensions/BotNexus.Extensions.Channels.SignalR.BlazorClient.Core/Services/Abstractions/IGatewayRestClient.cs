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

    /// <summary>
    /// GET /api/conversations?agentId={agentId} — returns conversations <em>relevant to</em>
    /// the agent: those it owns/initiated AND those where it appears as a participant
    /// (W-1 responder-side visibility, shipped in P9-G / issue #661).
    /// </summary>
    /// <remarks>
    /// The portal's <c>ClientStateStore.SeedConversations</c> filters returned summaries to
    /// <c>Kind == "HumanAgent"</c> before populating the sidebar — AgentAgent and AgentSubAgent
    /// kinds are intentionally hidden from the conversation drawer (they would clutter it and
    /// can auto-hijack the active tab on updates). Use the REST API directly for admin / debug
    /// views that need to see the full set.
    /// </remarks>
    Task<IReadOnlyList<ConversationSummaryDto>> GetConversationsAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// GET /api/conversations - returns the global active conversation summaries across every agent
    /// (no <c>agentId</c> filter), including participant rosters. Backs the Home / Activity dashboard
    /// so it can render one cross-platform activity view without fanning out a per-agent request.
    /// </summary>
    Task<IReadOnlyList<ConversationSummaryDto>> GetAllConversationsAsync(
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

    /// <summary>
    /// POST (pin) or DELETE (unpin) /api/conversations/{conversationId}/pin — toggles whether the
    /// conversation is pinned to the top of the sidebar list. Returns <c>true</c> on success.
    /// </summary>
    Task<bool> PinConversationAsync(string conversationId, bool pinned, CancellationToken ct = default);

    /// <summary>PUT /api/conversations/{conversationId}/override - set or clear the per-conversation model/thinking/context override (PBI5, #1706).</summary>
    Task<ConversationResponseDto?> SetConversationOverrideAsync(
        string conversationId,
        SetConversationOverrideRequestDto request,
        CancellationToken cancellationToken = default);

    /// <summary>DELETE /api/conversations/{conversationId}/override - clear all per-conversation overrides back to the agent default (PBI5, #1706).</summary>
    Task<ConversationResponseDto?> ClearConversationOverrideAsync(
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/agents/{agentId}/workspace or /api/agents/{agentId}/workspace/{path}</summary>
    Task<WorkspaceResponseDto?> GetWorkspaceAsync(
        string agentId,
        string? path = null,
        CancellationToken cancellationToken = default);

    /// <summary>DELETE /api/agents/{agentId}/workspace/{path}?force={force}</summary>
    Task<bool> DeleteWorkspaceItemAsync(
        string agentId,
        string path,
        bool force = false,
        CancellationToken cancellationToken = default);

    /// <summary>PUT /api/agents/{agentId}/workspace/{path} — write text content to a file.</summary>
    Task<bool> WriteWorkspaceFileAsync(
        string agentId,
        string path,
        string content,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/agents/{agentId}/reports</summary>
    Task<IReadOnlyList<ReportListItemDto>> GetReportsAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/agents/{agentId}/reports/{fileName}</summary>
    Task<ReportContentDto?> GetReportAsync(
        string agentId,
        string fileName,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/agents/{agentId}/conversations/{conversationId}/canvas</summary>
    /// <returns>The canvas HTML string, or null if none exists.</returns>
    Task<string?> GetConversationCanvasAsync(
        string agentId,
        string conversationId,
        CancellationToken ct = default);

    /// <summary>GET /api/agents/{agentId}/conversations/{conversationId}/todo</summary>
    /// <returns>The raw TodoJson document string, or null if the conversation has no todo state.</returns>
    Task<string?> GetConversationTodoAsync(
        string agentId,
        string conversationId,
        CancellationToken ct = default);

    /// <summary>GET /api/agents/{agentId}/conversations/{conversationId}/pending-ask-user</summary>
    /// <returns>
    /// The raw serialized <c>AskUserRequest</c> JSON for the conversation's pending prompt, or null
    /// when no prompt is waiting. Used to hydrate the inline prompt on connect for a reloaded,
    /// newly-opened, or mobile client that missed the live <c>UserInputRequired</c> event (#1488).
    /// </returns>
    Task<string?> GetConversationPendingAskUserAsync(
        string agentId,
        string conversationId,
        CancellationToken ct = default);

    /// <summary>GET /api/extensions/details — lists all loaded extensions with full manifest detail.</summary>
    Task<IReadOnlyList<ExtensionDetailDto>> GetExtensionDetailsAsync(CancellationToken cancellationToken = default);

    /// <summary>GET /api/skills or /api/skills/{path} — browse the global skills directory.</summary>
    Task<WorkspaceResponseDto?> GetSkillsAsync(string? path = null, CancellationToken cancellationToken = default);

    /// <summary>PUT /api/skills/{path} — write text content to a skills-relative file.</summary>
    Task<bool> WriteSkillFileAsync(string path, string content, CancellationToken cancellationToken = default);

    /// <summary>DELETE /api/skills/{path}?force={force} — delete a file or directory from the skills dir.</summary>
    Task<bool> DeleteSkillItemAsync(string path, bool force = false, CancellationToken cancellationToken = default);

    /// <summary>POST /api/diagnostics/channel-error - report a channel-side error to the gateway log.</summary>
    Task ReportChannelErrorAsync(ChannelErrorReportDto report, CancellationToken cancellationToken = default);

    /// <summary>GET /api/sessions/{sessionId}/debug?offset={offset}&amp;limit={limit}</summary>
    Task<SessionDebugSnapshotDto?> GetSessionDebugAsync(
        string sessionId,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>GET /api/sessions/{sessionId}/subagents — returns the live sub-agents for a session.</summary>
    Task<IReadOnlyList<SubAgentInfo>> ListSessionSubAgentsAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Current API base URL (set via Configure). Null if not yet configured.</summary>
    string? ApiBaseUrl { get; }
}
