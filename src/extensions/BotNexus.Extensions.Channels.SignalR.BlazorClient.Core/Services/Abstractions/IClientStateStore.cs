namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>
/// Single in-memory source of truth for all portal state.
/// Components subscribe to <see cref="OnChanged"/> to trigger re-renders.
/// </summary>
public interface IClientStateStore
{
    // ── Global ───────────────────────────────────────────────────────────────

    /// <summary>Fired whenever any state mutates. Components subscribe here for re-render.</summary>
    event Action? OnChanged;

    /// <summary>Fire <see cref="OnChanged"/> to trigger UI re-render.</summary>
    void NotifyChanged();

    // ── Agent-level ──────────────────────────────────────────────────────────

    /// <summary>All known agents keyed by agent ID.</summary>
    IReadOnlyDictionary<string, AgentState> Agents { get; }

    /// <summary>The currently visible/active agent tab.</summary>
    string? ActiveAgentId { get; set; }

    /// <summary>Seed initial agent list from hub or REST.</summary>
    void SeedAgents(IEnumerable<AgentSummary> agents);

    /// <summary>Get a single agent state by ID, or null if not found.</summary>
    AgentState? GetAgent(string agentId);

    /// <summary>Upsert agent state (adds or updates display name/connection).</summary>
    void UpsertAgent(AgentState agent);

    /// <summary>Remove an agent from the store by ID.</summary>
    void RemoveAgent(string agentId);

    // ── Conversation-level ───────────────────────────────────────────────────

    /// <summary>Seed the conversation list for a given agent.</summary>
    void SeedConversations(string agentId, IEnumerable<ConversationSummaryDto> conversations);

    /// <summary>Get a conversation state by conversation ID (searches all agents).</summary>
    ConversationState? GetConversation(string conversationId);

    /// <summary>Set active conversation for an agent, updating both agent and global selection.</summary>
    void SetActiveConversation(string agentId, string conversationId);

    /// <summary>The active conversation ID for the active agent.</summary>
    string? ActiveConversationId { get; }

    // ── Message operations ───────────────────────────────────────────────────

    /// <summary>Get messages for the given conversation.</summary>
    IReadOnlyList<ChatMessage> GetMessages(string conversationId);

    /// <summary>Append a message to the end of the conversation's timeline.</summary>
    void AppendMessage(string conversationId, ChatMessage message);

    /// <summary>Prepend older messages (pagination) to the start of the timeline.</summary>
    void PrependMessages(string conversationId, IEnumerable<ChatMessage> messages);

    /// <summary>Clear all messages for a conversation.</summary>
    void ClearMessages(string conversationId);

    // ── Session resolution ─────────────────────────────────────────────────────

    /// <summary>Register or update a session-ID → agent-ID mapping.</summary>
    void RegisterSession(string agentId, string sessionId, string? channelType = null, string? sessionType = null);

    /// <summary>Resolve a session ID to the agent ID that owns it. Returns false if unknown.</summary>
    bool TryResolveAgentBySession(string? sessionId, out string? agentId);

    /// <summary>
    /// Resolve a conversation ID to the agent ID that owns it (the agent whose
    /// <c>Conversations</c> dictionary contains the conversation). Returns false if unknown.
    /// PR1.5 (#682): post-compaction stream events carry a new sessionId the client has not
    /// yet registered; this fallback lets the handler still route by the surviving conversation.
    /// </summary>
    bool TryResolveAgentByConversation(string? conversationId, out string? agentId);

    /// <summary>
    /// Resolve a session ID to the conversation ID on the given agent whose
    /// <c>ActiveSessionId</c> matches.
    /// </summary>
    bool TryResolveConversationBySession(string agentId, string? sessionId, out string? conversationId);

    // ── Streaming state ──────────────────────────────────────────────────────

    /// <summary>Get streaming state for a conversation.</summary>
    ConversationStreamState GetStreamState(string conversationId);

    /// <summary>Set whether a conversation is currently streaming.</summary>
    void SetStreaming(string conversationId, bool streaming);

    /// <summary>Append a delta string to the conversation's stream buffer.</summary>
    void AppendStreamBuffer(string conversationId, string delta);

    /// <summary>Commit the stream buffer as a final assistant message in the conversation.</summary>
    void CommitStreamBuffer(string conversationId, string? thinkingContent = null);

    // ── ask_user prompt state ────────────────────────────────────────────────

    /// <summary>
    /// Set or replace the pending <c>ask_user</c> prompt for a conversation so
    /// the chat panel can render inline response controls.
    /// </summary>
    void SetPendingAskUser(AskUserPromptState prompt);

    /// <summary>
    /// Clear the pending <c>ask_user</c> prompt for a conversation after submit,
    /// cancellation, timeout, or stream termination.
    /// </summary>
    void ClearPendingAskUser(string conversationId);

    /// <summary>
    /// Get the active <c>ask_user</c> prompt for the conversation, or <c>null</c>
    /// when no interactive prompt is waiting.
    /// </summary>
    AskUserPromptState? GetPendingAskUser(string conversationId);

    // ── Steering queue ────────────────────────────────────────────────────────

    /// <summary>Add a steering entry to the pending queue for the given conversation.</summary>
    void AddSteeringEntry(string conversationId, SteeringEntry entry);

    /// <summary>Update the status of a steering entry in the given conversation's queue.</summary>
    void UpdateSteeringEntry(string conversationId, string entryId, SteeringEntryStatus newStatus);

    /// <summary>Get the pending steering queue for a conversation.</summary>
    IReadOnlyList<SteeringEntry> GetSteeringQueue(string conversationId);
}

/// <summary>Agent-level state for the portal sidebar and chat panel.</summary>
public sealed class AgentState
{
    /// <summary>Unique agent identifier.</summary>
    public required string AgentId { get; init; }

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Optional emoji that visually identifies this agent.</summary>
    public string? Emoji { get; set; }

    /// <summary>Short description of this agent's purpose.</summary>
    public string? Description { get; set; }

    /// <summary>Whether this is a platform built-in agent (sorts after user agents in UI).</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>Active session ID (last established).</summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// The session ID for the currently active conversation.
    /// Prefers the active conversation's ActiveSessionId over the agent-level SessionId.
    /// Use this for actions that target the current conversation (Steer, Abort, Reset, Compact).
    /// </summary>
    public string? ActiveConversationSessionId =>
        ActiveConversationId is not null && Conversations.TryGetValue(ActiveConversationId, out var conv)
            ? conv.ActiveSessionId ?? SessionId
            : SessionId;

    /// <summary>Channel type for this session.</summary>
    public string? ChannelType { get; set; }

    /// <summary>Session type — user-agent, agent-subagent, etc.</summary>
    public string SessionType { get; set; } = "user-agent";

    /// <summary>Whether this session is read-only (sub-agent observer).</summary>
    public bool IsReadOnly => SessionType == "agent-subagent";

    /// <summary>Whether the SignalR hub connection is live.</summary>
    public bool IsConnected { get; set; }

    /// <summary>Whether this agent is actively streaming a response.</summary>
    public bool IsStreaming { get; set; }

    /// <summary>Count of unread messages while this agent is not the active tab.</summary>
    public int UnreadCount { get; set; }

    /// <summary>Current processing stage description for the status bar.</summary>
    public string? ProcessingStage { get; set; }

    /// <summary>Whether tool messages are visible in the chat panel.</summary>
    public bool ShowTools { get; set; } = true;

    /// <summary>Whether thinking blocks are visible in the chat panel.</summary>
    public bool ShowThinking { get; set; } = true;

    /// <summary>The currently selected conversation ID for this agent.</summary>
    public string? ActiveConversationId { get; set; }

    /// <summary>Whether the conversation list has been loaded from REST.</summary>
    public bool ConversationsLoaded { get; set; }

    /// <summary>Whether a conversation list fetch is currently in-flight.</summary>
    public bool IsLoadingConversations { get; set; }

    /// <summary>All conversations for this agent keyed by conversation ID.</summary>
    public Dictionary<string, ConversationState> Conversations { get; } = new();

    /// <summary>In-progress tool calls keyed by tool-call ID.</summary>
    public Dictionary<string, ActiveToolCall> ActiveToolCalls { get; } = new();

    /// <summary>Sub-agents spawned by this agent keyed by sub-agent ID.</summary>
    public Dictionary<string, SubAgentInfo> SubAgents { get; } = new();

    /// <summary>Latest HTML payload published to the Canvas tab for this agent.</summary>
    public string? CanvasHtml { get; set; }

    /// <summary>When the latest canvas payload was published.</summary>
    public DateTimeOffset? CanvasUpdatedAt { get; set; }
}

/// <summary>Conversation-level state: messages, stream buffers, pagination flags.</summary>
public sealed class ConversationState
{
    /// <summary>Unique conversation identifier.</summary>
    public required string ConversationId { get; init; }

    /// <summary>Display title.</summary>
    public string Title { get; set; } = "New conversation";

    /// <summary>Whether this is the agent's default conversation.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Whether this conversation is pinned to the top.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Conversation status (Active, Archived, etc.).</summary>
    public string Status { get; set; } = "Active";

    /// <summary>The live session ID associated with this conversation.</summary>
    public string? ActiveSessionId { get; set; }

    /// <summary>Count of unread messages while this conversation is not active.</summary>
    public int UnreadCount { get; set; }

    /// <summary>When the conversation was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the conversation was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when this is a virtual read-only session row (for example, cron).</summary>
    public bool IsVirtualSession { get; set; }

    /// <summary>Virtual session kind label (for example, "cron"). Null for normal conversations.</summary>
    public string? VirtualSessionKind { get; set; }


    // ── Canvas ──────────────────────────────────────────────────────────────────

    /// <summary>Latest HTML payload published to the Canvas tab for this conversation.</summary>
    public string? CanvasHtml { get; set; }

    /// <summary>When the latest canvas payload was published to this conversation.</summary>
    public DateTimeOffset? CanvasUpdatedAt { get; set; }

    // ── History flags ────────────────────────────────────────────────────────

    /// <summary>Whether the latest page of history has been loaded.</summary>
    public bool HistoryLoaded { get; set; }

    /// <summary>Whether a history fetch is currently in-flight.</summary>
    public bool IsLoadingHistory { get; set; }

    /// <summary>Whether there are older messages available for pagination.</summary>
    public bool HasMoreHistory { get; set; }

    /// <summary>Cursor for fetching the next (older) page of history.</summary>
    public string? NextBeforeCursor { get; set; }

    // ── Messages + streaming ─────────────────────────────────────────────────

    /// <summary>Messages in this conversation's timeline.</summary>
    public List<ChatMessage> Messages { get; } = new();

    /// <summary>Streaming state for this conversation.</summary>
    public ConversationStreamState StreamState { get; } = new();

    /// <summary>Pending steering entries for this conversation's queue panel.</summary>
    public List<SteeringEntry> PendingSteeringQueue { get; } = new();
}


/// <summary>Stream-buffer state for an active or recently active conversation.</summary>
public sealed class ConversationStreamState
{
    /// <summary>Whether the conversation is currently receiving a streaming response.</summary>
    public bool IsStreaming { get; set; }

    /// <summary>
    /// Whether the agent run loop is active for this conversation. Set <see langword="true"/> on the
    /// <c>RunStarted</c> event and <see langword="false"/> on <c>RunEnded</c> — the authoritative
    /// signal that brackets the ENTIRE loop (all turns, tool cycles, follow-up continuations).
    /// </summary>
    /// <remarks>
    /// This is the primary driver of <see cref="IsTurnActive"/>. Unlike <see cref="IsStreaming"/> and
    /// <see cref="ActiveToolCalls"/> (which only cover individual steps and drop to a quiescent state
    /// in the gaps between message-end/tool-start, tool-end/tool-start, and tool-end/next-message-start),
    /// <c>IsRunActive</c> stays asserted across those gaps so steer/follow-up/stop controls don't flicker.
    /// The streaming/tool fields are retained as a defensive fallback for clients that miss the
    /// RunStarted/RunEnded bracket (e.g. an older gateway, or a reconnect mid-run).
    /// </remarks>
    public bool IsRunActive { get; set; }

    /// <summary>Accumulated content delta buffer during streaming.</summary>
    public string Buffer { get; set; } = "";

    /// <summary>Accumulated thinking-content buffer during streaming.</summary>
    public string ThinkingBuffer { get; set; } = "";

    /// <summary>In-progress tool calls for this conversation keyed by tool-call ID.</summary>
    public Dictionary<string, ActiveToolCall> ActiveToolCalls { get; } = new();

    /// <summary>Whether the agent turn is still active -- the run loop is executing, streaming, or
    /// awaiting tool results. Use this instead of IsStreaming to keep Steer/Follow Up/Stop controls
    /// visible across the whole loop, including the gaps between the end of an LLM generation, tool
    /// execution, and the next generation.</summary>
    public bool IsTurnActive => IsRunActive || IsStreaming || ActiveToolCalls.Count > 0;

    /// <summary>
    /// Clears the streaming buffers and the <see cref="IsStreaming"/> flag atomically.
    /// Every terminal handler (message-end, error, turn-interrupted, turn-end, session-reset,
    /// reconnect) MUST call this rather than clearing the three fields by hand -- the portal
    /// must never get stuck in a perpetual streaming indicator if one field is forgotten
    /// (recurring regression class: #456, #668, #759). Centralising the reset makes that
    /// invariant a single method a future handler cannot half-apply. Active tool calls are
    /// intentionally left untouched so <see cref="IsTurnActive"/> stays accurate while tools run.
    /// </summary>
    public void Reset()
    {
        IsStreaming = false;
        Buffer = "";
        ThinkingBuffer = "";
    }

    /// <summary>
    /// Clears ALL run state for a genuinely terminal event: the streaming buffers, the
    /// <see cref="IsRunActive"/> bracket, and any lingering <see cref="ActiveToolCalls"/>. Call this
    /// (not <see cref="Reset"/>) only when the entire run loop is over — <c>RunEnded</c>, or a
    /// truly terminal fallback (error, turn-interrupted, session-reset, reconnect). Per-turn terminal
    /// events (message-end, turn-end) must call <see cref="Reset"/> instead, because the loop may
    /// continue with more turns/tools and <see cref="IsRunActive"/> must stay asserted across them.
    /// </summary>
    public void EndRun()
    {
        Reset();
        IsRunActive = false;
        ActiveToolCalls.Clear();
    }
}

