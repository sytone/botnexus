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

    /// <summary>
    /// Request a UI re-render via <see cref="OnChanged"/>, time-coalesced for the high-frequency
    /// streaming-delta path: a burst of calls collapses to at most one render per throttle window
    /// so a long streamed response does not trigger one re-render per token (#1620). Use this only
    /// for content/thinking deltas; discrete lifecycle events must use <see cref="NotifyChanged"/>
    /// so they are never delayed.
    /// </summary>
    void NotifyChangedThrottled();

    // ── Agent-level ──────────────────────────────────────────────────────────

    /// <summary>All known agents keyed by agent ID.</summary>
    IReadOnlyDictionary<string, AgentState> Agents { get; }

    /// <summary>The currently visible/active agent tab. Read-only projection of the single
    /// <see cref="ViewSelection"/> the store holds; mutate it only via <see cref="SelectView"/> (#2246).</summary>
    string? ActiveAgentId { get; }

    /// <summary>
    /// True when the last active selection was invalidated by an inbound event (the active agent was
    /// removed, or its active conversation 404'd server-side) and no replacement has been chosen yet.
    /// Inbound handlers set this instead of mutating the active view themselves; the UI observes it on
    /// the next render and resolves a fresh selection via <see cref="SelectView"/> (#2246).
    /// </summary>
    bool PendingSelectionInvalid { get; }

    /// <summary>
    /// Signals that the current active selection has been invalidated by an inbound event (e.g. its
    /// active conversation 404'd server-side) without choosing a replacement view. Sets
    /// <see cref="PendingSelectionInvalid"/>; the UI resolves a fresh selection on next render (#2246).
    /// </summary>
    void MarkSelectionInvalid();

    /// <summary>
    /// The single mutation path for the active view. Sets the active agent and (when supplied) the
    /// active conversation atomically, tagged with the <see cref="SelectionSource"/> that requested it.
    /// Only <see cref="SelectionSource.SubAgentView"/> may switch the active view onto a read-only
    /// sub-agent session — every other source is rejected when the target is read-only, so a concurrent
    /// <c>SubAgentSpawned</c> or streaming event can never hijack the active view onto a sub-agent
    /// session (#2243). Pass an empty <paramref name="conversationId"/> when only the agent is known.
    /// </summary>
    /// <param name="agentId">The agent to make active, or empty to clear the active view.</param>
    /// <param name="conversationId">The conversation to activate, or empty when unspecified.</param>
    /// <param name="source">Which interaction requested this selection.</param>
    void SelectView(string agentId, string conversationId, SelectionSource source);

    /// <summary>
    /// Records <paramref name="subAgentId"/> as a sub-agent session so the anti-hijack guard in
    /// <see cref="SelectView"/> rejects any non-<see cref="SelectionSource.SubAgentView"/> switch onto
    /// it, even before its <see cref="AgentState"/> exists or its SessionType has been stamped
    /// read-only. Call this at sub-agent spawn time. Idempotent (#2243, folded via #2246).
    /// </summary>
    /// <param name="subAgentId">The spawned sub-agent id to mark read-only for navigation.</param>
    void MarkSubAgent(string subAgentId);

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

    /// <summary>
    /// Register or update a session-ID → agent-ID mapping.
    /// </summary>
    /// <param name="agentId">The owning agent.</param>
    /// <param name="sessionId">The session being registered.</param>
    /// <param name="channelType">Optional channel type for the session.</param>
    /// <param name="sessionType">Optional session type (e.g. <c>cron</c>, <c>user-agent</c>).</param>
    /// <param name="conversationId">
    /// The conversation this session belongs to, when known (e.g. from the <c>/api/sessions</c>
    /// projection). When supplied, the session is bound to <em>that</em> conversation rather than
    /// the agent's currently-active one, and the agent-global <c>SessionId</c> is only updated when
    /// the session belongs to the active conversation. This prevents a bulk session refresh from
    /// stamping the last-iterated session onto the wrong conversation — the root cause of steer /
    /// abort / compact actions targeting a different conversation's session.
    /// When <see langword="null"/>, the legacy single-establish behaviour is preserved: the session
    /// binds to the active conversation (race fix #314).
    /// </summary>
    void RegisterSession(string agentId, string sessionId, string? channelType = null, string? sessionType = null, string? conversationId = null);

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

    // ── Todo ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Latest persisted todo state for this conversation (the raw <c>TodoJson</c> document, or null
    /// when there is no plan). Drives the Todo panel; hydrated from REST and refreshed live via the
    /// <c>TodoUpdated</c> SignalR event (#1464 step 5).
    /// </summary>
    public string? TodoJson { get; set; }

    /// <summary>When the latest todo payload was applied to this conversation.</summary>
    public DateTimeOffset? TodoUpdatedAt { get; set; }

    // ── History flags ────────────────────────────────────────────────────────

    /// <summary>Whether the latest page of history has been loaded.</summary>
    public bool HistoryLoaded { get; set; }

    /// <summary>Whether a history fetch is currently in-flight.</summary>
    public bool IsLoadingHistory { get; set; }

    /// <summary>Whether the last history fetch failed (non-404 error). Drives the load-error empty
    /// state in the message view so a blank pane is never ambiguous between empty and broken (#1697).</summary>
    public bool HistoryLoadFailed { get; set; }

    /// <summary>Whether there are older messages available for pagination.</summary>
    public bool HasMoreHistory { get; set; }

    /// <summary>Cursor for fetching the next (older) page of history.</summary>
    public string? NextBeforeCursor { get; set; }

    /// <summary>Count of real history rows fetched so far for this conversation. Doubles as the
    /// offset for the next backwards page (#1691): the load-more fetch reads at this offset,
    /// prepends, then advances it by the page count. Boundary/compaction dividers are synthesised
    /// locally and are intentionally not counted here.</summary>
    public int LoadedHistoryRows { get; set; }

    // ── Messages + streaming ─────────────────────────────────────────────────

    // Backing store for the conversation timeline. Kept private so every mutation flows through the
    // helpers below, which keep _messageIndex in sync. Exposing the raw List would let a caller
    // Add/Remove/Clear directly and silently desync the id->index map (#1622).
    private readonly List<ChatMessage> _messages = new();

    // O(1) id -> first-occurrence index map over _messages. Lets HandleToolEnd locate the message a
    // tool result belongs to without an O(n) FindIndex scan on every ToolEnd (#1622). Only messages
    // with a non-empty Id are indexed; on a duplicate Id the FIRST index wins, exactly mirroring
    // List.FindIndex semantics so the lookup is behaviour-preserving.
    private readonly Dictionary<string, int> _messageIndex = new(StringComparer.Ordinal);

    /// <summary>Messages in this conversation's timeline. Read-only view: mutate via
    /// <see cref="AppendMessage"/>, <see cref="ReplaceMessageAt"/>, <see cref="PrependMessages"/>,
    /// or <see cref="ClearMessages"/> so the id-&gt;index map stays consistent (#1622).</summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>O(1) id-&gt;index map over <see cref="Messages"/>. Each non-empty message id maps to the
    /// first index it appears at (matching <c>List.FindIndex</c>). Used by the ToolEnd handler to locate
    /// the message a tool result belongs to without a linear scan (#1622).</summary>
    public IReadOnlyDictionary<string, int> MessageIndex => _messageIndex;

    /// <summary>Appends a message to the timeline and indexes it in O(1).</summary>
    public void AppendMessage(ChatMessage message)
    {
        _messages.Add(message);
        IndexMessageAt(_messages.Count - 1);
    }

    /// <summary>Replaces the message at <paramref name="index"/> in place (used by the ToolEnd update
    /// path, which rewrites a tool-call row via <c>original with { ... }</c>). Keeps the id-&gt;index map
    /// consistent: the position is unchanged, and if the replacement carries a different id the map is
    /// updated to point the new id at this slot.</summary>
    public void ReplaceMessageAt(int index, ChatMessage message)
    {
        if (index < 0 || index >= _messages.Count)
            return;

        var previous = _messages[index];
        _messages[index] = message;

        // If the id changed, drop the old id's entry only when it pointed at this slot (a later
        // duplicate may still own the old id), then index the slot under the new id.
        if (!string.Equals(previous.Id, message.Id, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(previous.Id)
                && _messageIndex.TryGetValue(previous.Id, out var owned)
                && owned == index)
            {
                _messageIndex.Remove(previous.Id);
            }

            IndexMessageAt(index);
        }
    }

    /// <summary>Prepends older messages (used for history pagination). Every existing index shifts, so
    /// the id-&gt;index map is rebuilt.</summary>
    public void PrependMessages(IEnumerable<ChatMessage> messages)
    {
        _messages.InsertRange(0, messages);
        RebuildMessageIndex();
    }

    /// <summary>Clears the timeline and the id-&gt;index map.</summary>
    public void ClearMessages()
    {
        _messages.Clear();
        _messageIndex.Clear();
    }

    /// <summary>Resolves a message id to its index in O(1). Returns <see langword="false"/> for a
    /// null/empty id or an id not present, leaving <paramref name="index"/> at -1 -- the same graceful
    /// miss the previous <c>FindIndex(...) == -1</c> path produced (#1622).</summary>
    public bool TryGetMessageIndex(string? id, out int index)
    {
        if (!string.IsNullOrEmpty(id) && _messageIndex.TryGetValue(id, out index))
            return true;

        index = -1;
        return false;
    }

    // Indexes the message at the given slot under its id, keeping FIRST-occurrence-wins semantics so a
    // duplicate id never overwrites the earlier mapping (List.FindIndex returns the first match).
    private void IndexMessageAt(int index)
    {
        var id = _messages[index].Id;
        if (!string.IsNullOrEmpty(id))
            _messageIndex.TryAdd(id, index);
    }

    // Full O(n) rebuild after a bulk reorder (prepend). Cheaper and simpler than shifting every entry,
    // and prepend is a rare pagination event, not on the streaming hot path.
    private void RebuildMessageIndex()
    {
        _messageIndex.Clear();
        for (var i = 0; i < _messages.Count; i++)
            IndexMessageAt(i);
    }

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

    /// <summary>Accumulated content delta buffer during streaming. Backed by a
    /// <see cref="System.Text.StringBuilder"/> so accumulating thousands of streamed deltas via
    /// <see cref="AppendBuffer"/> is amortised O(1) per delta rather than an O(n) copy of the
    /// growing reply on every token (#1620). The setter replaces the whole buffer (used for test
    /// seeding and bulk resets) and is NOT on the streaming hot path.</summary>
    public string Buffer
    {
        get => _buffer.ToString();
        set { _buffer.Clear(); if (!string.IsNullOrEmpty(value)) _buffer.Append(value); }
    }

    /// <summary>Accumulated thinking-content buffer during streaming. Backed by a
    /// <see cref="System.Text.StringBuilder"/> for the same reason as <see cref="Buffer"/>.</summary>
    public string ThinkingBuffer
    {
        get => _thinkingBuffer.ToString();
        set { _thinkingBuffer.Clear(); if (!string.IsNullOrEmpty(value)) _thinkingBuffer.Append(value); }
    }

    private readonly System.Text.StringBuilder _buffer = new();
    private readonly System.Text.StringBuilder _thinkingBuffer = new();

    /// <summary>
    /// Role the currently-buffered content should be committed under when the stream flushes
    /// (#1651). <see langword="null"/> -- the default and overwhelmingly common case -- means
    /// "no override", so the terminal flush commits an <c>Assistant</c> message exactly as it
    /// did before post-as-assistant. Set from the <c>role</c> field on a <c>ContentDelta</c>
    /// event when the live SignalR fan-out delivers an agent-post the gateway stamped with a
    /// specific role (e.g. <c>user</c> for an on-behalf-of-user kickoff), so the buffered post
    /// renders as a user bubble rather than being forced to assistant. Cleared with the buffers
    /// on every reset so a role from one post never bleeds into the next turn.
    /// </summary>
    public string? PendingRole { get; set; }

    /// <summary>Append a content delta to <see cref="Buffer"/>. A <see langword="null"/>
    /// delta is treated as empty. O(1) amortised -- does not copy the accumulated reply.</summary>
    public void AppendBuffer(string? delta)
    {
        if (!string.IsNullOrEmpty(delta))
            _buffer.Append(delta);
    }

    /// <summary>Append a thinking-content delta to <see cref="ThinkingBuffer"/>. A
    /// <see langword="null"/> delta is treated as empty. O(1) amortised.</summary>
    public void AppendThinking(string? delta)
    {
        if (!string.IsNullOrEmpty(delta))
            _thinkingBuffer.Append(delta);
    }

    /// <summary>Clear the content and thinking buffers without touching the streaming flags.
    /// Used at the start of a fresh streamed message (<see cref="IsStreaming"/> stays asserted).</summary>
    public void ClearBuffers()
    {
        _buffer.Clear();
        _thinkingBuffer.Clear();
        PendingRole = null;
    }

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
        _buffer.Clear();
        _thinkingBuffer.Clear();
        PendingRole = null;
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

