namespace BotNexus.Gateway.Api.Controllers;

/// <summary>Request body for creating a conversation.</summary>
/// <param name="AgentId">The agent that owns the new conversation.</param>
/// <param name="Title">Optional display title. When omitted, a default title is assigned.</param>
/// <param name="Purpose">Optional description of the conversation's intended use.</param>
/// <param name="Instructions">Optional conversation-scoped instructions injected into the system prompt on session start.</param>
public sealed record CreateConversationRequest(string AgentId, string? Title, string? Purpose = null, string? Instructions = null);

/// <summary>Request body for patching conversation metadata.</summary>
/// <param name="Title">Optional replacement display title.</param>
/// <param name="Purpose">Optional replacement description of the conversation's intended use.</param>
/// <param name="Instructions">Optional conversation-scoped instructions injected into the system prompt. Pass null to clear.</param>
public sealed record PatchConversationRequest(string? Title = null, string? Purpose = null, string? Instructions = null);

/// <summary>Request body for adding a channel binding.</summary>
public sealed record AddBindingRequest(
    string ChannelType,
    string? ChannelAddress,
    string? Mode,
    string? ThreadingMode,
    string? DisplayPrefix);

/// <summary>
/// Request body for moving an existing channel binding to a different conversation
/// (issue #140). The binding keeps its identity, address and mode; only its owning
/// conversation changes, so channel history and routing continuity are preserved.
/// </summary>
/// <param name="TargetConversationId">The conversation to re-parent the binding onto. Must exist, must belong to the same agent, and must not already hold a binding for the same (channel type, address) pair.</param>
public sealed record MoveBindingRequest(string TargetConversationId);

/// <summary>Full conversation response including bindings.</summary>
/// <remarks>
/// <c>Kind</c>, <c>Source</c> and <c>Visibility</c> are the orthogonal provenance axes a client needs
/// to render a conversation without inference (epic #2300, issue #2340): <c>Kind</c> is the pairing
/// topology, <c>Source</c>
/// is the origination trigger, <c>Visibility</c> is who may see it. All are emitted as the enum
/// <em>name</em>, matching the existing
/// string convention already used for <c>Status</c> on this contract.
/// </remarks>
public sealed record ConversationResponse(
    string ConversationId,
    string AgentId,
    string Title,
    string? Purpose,
    string? Instructions,
    bool IsDefault,
    string Status,
    string? ActiveSessionId,
    IReadOnlyList<BindingResponse> Bindings,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ModelOverride = null,
    string? ThinkingOverride = null,
    int? ContextWindowOverride = null,
    string Kind = "HumanAgent",
    string Source = "Channel",
    string Visibility = "UserFacing");

/// <summary>
/// Request body for setting or clearing a conversation's model / thinking / context override
/// (PBI5, issue #1706). Each field is independent: a <c>null</c> field clears that single
/// override back to the agent default, while a non-null field sets it. Sending all three as
/// <c>null</c> clears every conversation-level override.
/// </summary>
/// <param name="Model">Optional model-id override; <c>null</c> clears the model override.</param>
/// <param name="Thinking">Optional thinking-level wire token (e.g. <c>high</c>, <c>max</c>); <c>null</c> clears the thinking override.</param>
/// <param name="ContextWindow">Optional context-window override in tokens; <c>null</c> clears the context override.</param>
public sealed record SetConversationOverrideRequest(
    string? Model = null,
    string? Thinking = null,
    int? ContextWindow = null);

/// <summary>Channel binding response.</summary>
public sealed record BindingResponse(
    string BindingId,
    string ChannelType,
    string ChannelAddress,
    string Mode,
    string ThreadingMode,
    string? DisplayPrefix,
    DateTimeOffset BoundAt);

/// <summary>Response returned by <c>POST /api/conversations/{id}/reset</c>.</summary>
/// <param name="ConversationId">The conversation that was reset.</param>
/// <param name="Outcome">The reset outcome: <c>Reset</c>, <c>NoActiveSession</c>, <c>NotFound</c>, or <c>StaleSessionId</c>.</param>
/// <param name="SealedSessionId">The session id that was sealed, when <paramref name="Outcome"/> is <c>Reset</c>; otherwise <c>null</c>.</param>
public sealed record ConversationResetResponse(
    string ConversationId,
    string Outcome,
    string? SealedSessionId);

/// <summary>Paginated conversation history response.</summary>
public sealed record ConversationHistoryResponse(
    string ConversationId,
    int TotalCount,
    int Offset,
    int Limit,
    IReadOnlyList<ConversationHistoryEntry> Entries);

/// <summary>A single history entry — either a message or a session boundary marker.</summary>
public sealed class ConversationHistoryEntry
{
    /// <summary>Entry kind: "message" or "boundary".</summary>
    public required string Kind { get; init; }

    /// <summary>The session this entry belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>The agent that owns the session this entry belongs to.</summary>
    public string? AgentId { get; init; }

    /// <summary>Entry timestamp.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Message role (for kind = "message").</summary>
    public string? Role { get; init; }

    /// <summary>Message content (for kind = "message").</summary>
    public string? Content { get; init; }

    /// <summary>Tool name (for kind = "message" with tool role).</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool call correlation ID (for kind = "message" with tool role).</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Serialized JSON tool arguments (for ToolStart entries).</summary>
    public string? ToolArgs { get; init; }

    /// <summary>True if the tool call returned an error.</summary>
    public bool ToolIsError { get; init; }

    /// <summary>Reason for the boundary (for kind = "boundary").</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// True when the underlying transcript entry has been folded into a later compaction summary
    /// (<c>SessionEntry.IsHistory</c>). Folded entries are still returned so the transcript remains
    /// reachable from the portal (#2936) - compaction removes an entry from the LLM context window,
    /// it does not delete it - but the client renders them collapsed beneath the compaction boundary
    /// rather than as ordinary turns.
    /// </summary>
    public bool IsFolded { get; init; }

    /// <summary>Thinking/reasoning content from the model (for assistant messages).</summary>
    public string? ThinkingContent { get; init; }

    /// <summary>
    /// Origin attribution for this entry (issue #2840) - e.g. <c>api:cron:pr-doctor</c> for a message
    /// posted through <c>POST /api/agents/{agentId}/conversations/{conversationId}/messages</c>.
    /// <c>null</c> for ordinary channel turns and for every entry persisted before #2840.
    /// </summary>
    /// <remarks>
    /// Caller-supplied display text. Render it as provenance; never treat it as an identity claim.
    /// </remarks>
    public string? SenderId { get; init; }

    /// <summary>
    /// Orthogonal, typed presentation/delivery kind of the underlying transcript entry (issue
    /// #2149): <c>message</c> (ordinary/default), <c>subagent-completion</c> (the internal
    /// completion notification), or <c>subagent-response</c> (the parent agent's response to it).
    /// Distinct from <see cref="Kind"/>, which discriminates the history-envelope shape
    /// (<c>message</c> vs <c>compaction</c>). Always emitted so replay recovers the distinction a
    /// portal/channel needs without inferring it from <see cref="Role"/>, ids, or content.
    /// </summary>
    public string? MessageKind { get; init; }
}
