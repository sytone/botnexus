using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A gateway-level session that tracks an interaction between a caller and an agent.
/// Sessions own the conversation history and bridge the Gateway with the underlying
/// <c>AgentCore.Agent</c> execution.
/// </summary>
public sealed class GatewaySession
{
    private readonly Lock _runtimeLock = new();
    private GatewaySessionRuntime? _runtime;
    private string? _callerId;
    private readonly ISecretRedactor? _redactor;
    private AgentId _agentId;

    public GatewaySession()
        : this(new Session(), null)
    {
    }

    public GatewaySession(Session session, ISecretRedactor? redactor = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _redactor = redactor;
    }

    /// <summary>Domain session state for persistence.</summary>
    public Session Session { get; }

    /// <summary>Infrastructure runtime state for thread-safe mutation and replay buffering.</summary>
    public GatewaySessionRuntime Runtime
    {
        get
        {
            lock (_runtimeLock)
            {
                _runtime ??= new GatewaySessionRuntime(Session);
                return _runtime;
            }
        }
    }

    /// <summary>Unique session identifier.</summary>
    public SessionId SessionId
    {
        get => Session.SessionId;
        init => Session.SessionId = value;
    }

    /// <summary>
    /// The agent that owns the parent <see cref="Conversation"/> this session belongs to.
    /// Hydrated at construction time by <see cref="ISessionStore"/> implementations from
    /// <c>Conversation.AgentId</c> — the conversation is the durable owner of the agent
    /// binding (P9-H, issue #662, directive W-4). The value is immutable post-construction:
    /// <c>Conversation.AgentId</c> is itself <c>init</c>-only so a hydrated AgentId cannot
    /// drift for the lifetime of the session. Callers that need to resolve the AgentId
    /// for a session they do not yet have an instance of must inject
    /// <see cref="IAgentIdentityResolver"/>.
    /// </summary>
    public AgentId AgentId
    {
        get => _agentId;
        init => _agentId = value;
    }

    /// <summary>
    /// Hydrates the derived <see cref="AgentId"/> for an instance produced outside an
    /// object initializer (e.g. by <see cref="FromSession"/> and store load paths that
    /// build the wrapper first and look up the conversation second). This is the only
    /// sanctioned mutation path; the architecture fence pins it (
    /// <c>SessionAgentIdRemovedArchitectureTests</c>) so nothing outside the gateway
    /// session-store assembly can re-introduce a write-through facade.
    /// </summary>
    /// <param name="agentId">The resolved owning agent id.</param>
    public void HydrateAgentId(AgentId agentId) => _agentId = agentId;

    /// <summary>The channel this session originated from (e.g., "signalr", "telegram").</summary>
    public ChannelKey? ChannelType
    {
        get => Session.ChannelType;
        set => Session.ChannelType = value;
    }

    /// <summary>Caller-specific identifier within the channel (e.g., user ID, chat ID).</summary>
    public string? CallerId
    {
        get => _callerId;
        set => _callerId = value;
    }

    /// <summary>Session type discriminator.</summary>
    public SessionType SessionType
    {
        get => Session.SessionType;
        set => Session.SessionType = value;
    }

    /// <summary>
    /// Conversation this session belongs to. Always mutate through this proxy — the
    /// <see cref="GatewaySessionFacadeArchitectureTests"/> fence bans reach-through
    /// access via <c>session.Session.ConversationId</c> so the proxy stays the
    /// single source of truth (F-9 / Phase 7). The unset sentinel is
    /// <c>default(ConversationId)</c>; store implementations are responsible for
    /// backfilling unset values before returning a session to callers
    /// (Phase 9 / P9-B; issues #615, #627).
    /// </summary>
    public ConversationId ConversationId
    {
        get => Session.ConversationId;
        set => Session.ConversationId = value;
    }

    /// <summary>Computed interactivity marker.</summary>
    public bool IsInteractive => Session.IsInteractive;

    // P9-F (#657): GatewaySession.Participants facade was deleted along with the
    // underlying Session.Participants field. Participants now live on Conversation; see
    // IConversationStore.AddParticipantsAsync and ListForCitizenAsync for the new APIs.

    /// <summary>When the session was created.</summary>
    public DateTimeOffset CreatedAt
    {
        get => Session.CreatedAt;
        init => Session.CreatedAt = value;
    }

    /// <summary>When the session was last active.</summary>
    public DateTimeOffset UpdatedAt
    {
        get => Session.UpdatedAt;
        set => Session.UpdatedAt = value;
    }

    /// <summary>Current lifecycle status of the session.</summary>
    public SessionStatus Status
    {
        get => Session.Status;
        set => Session.Status = value;
    }

    /// <summary>Timestamp when this session expires, if known.</summary>
    public DateTimeOffset? ExpiresAt
    {
        get => Session.ExpiresAt;
        set => Session.ExpiresAt = value;
    }

    /// <summary>
    /// Conversation history as serializable entries.
    /// This is the Gateway-level view; the AgentCore maintains its own message timeline.
    /// </summary>
    public List<SessionEntry> History
    {
        get => Session.History;
        init => Session.History = value;
    }

    /// <summary>Number of entries in the conversation history.</summary>
    public int MessageCount => Session.MessageCount;

    /// <summary>
    /// The last system prompt rendered for this session at dispatch time.
    /// In-memory only — cleared on gateway restart. Set by the isolation
    /// strategy on handle creation so the debug inspector can retrieve it.
    /// </summary>
    public string? LastRenderedSystemPrompt { get; set; }

    /// <summary>
    /// The timestamp at which <see cref="LastRenderedSystemPrompt"/> was last captured.
    /// </summary>
    public DateTimeOffset? LastRenderedSystemPromptAt { get; set; }

    /// <summary>Session-level metadata for extensibility.</summary>
    public Dictionary<string, object?> Metadata
    {
        get => Session.Metadata;
        init => Session.Metadata = value;
    }

    /// <summary>
    /// Typed agent-to-agent exchange-completion state (issue #612, CC-1). Facade over
    /// <see cref="Session"/>.<see cref="Session.ExchangeCompletion"/>; replaces the four loose
    /// exchange-completion metadata string keys with a single typed nullable record. Mutate
    /// through this proxy so the reach-through fence stays satisfied.
    /// </summary>
    public AgentExchangeCompletionState? ExchangeCompletion
    {
        get => Session.ExchangeCompletion;
        set => Session.ExchangeCompletion = value;
    }

    /// <summary>
    /// Dedicated outbound-stream reconnect-replay peer for this session. The 8
    /// stream-replay members previously hosted on the facade (#575) collapsed
    /// here so the conversational session surface is no longer mixed with
    /// outbound-stream replay infrastructure. The gateway is transport-agnostic
    /// — channels (SignalR, Teams, ...) own how outbound payloads reach a user;
    /// this peer only records the sequenced stream for replay on reconnect.
    /// </summary>
    public SessionStreamReplay StreamReplay => Runtime.StreamReplay;

    /// <summary>
    /// Removes any crash-sentinel entries from the session history.
    /// Call on clean turn completion to prevent sentinels from persisting after a successful run.
    /// </summary>
    public void RemoveCrashSentinels() => Runtime.RemoveCrashSentinels();

    /// <summary>Thread-safe append to conversation history. Content is redacted before storage when a redactor is configured.</summary>
    public void AddEntry(SessionEntry entry) => Runtime.AddEntry(Redact(entry));

    /// <summary>Thread-safe append of multiple entries. Content is redacted before storage when a redactor is configured.</summary>
    public void AddEntries(IEnumerable<SessionEntry> entries) => Runtime.AddEntries(entries.Select(Redact));

    /// <summary>
    /// Thread-safe atomic append + snapshot. Equivalent to
    /// <see cref="AddEntry"/> followed by <see cref="SnapshotHistoryForCompaction"/>
    /// but performed under a single runtime lock so the appended entry is
    /// guaranteed to be at <c>Snapshot.Count - 1</c> regardless of concurrent
    /// destructive mutations, AND the pre-append
    /// <see cref="UpdatedAt"/> value is captured atomically with the append.
    /// Content is redacted before storage when a redactor is configured.
    /// </summary>
    public SessionAppendResult AddEntryAndSnapshot(SessionEntry entry) => Runtime.AddEntryAndSnapshot(Redact(entry));

    /// <summary>Replaces the session history with a compacted version.</summary>
    public void ReplaceHistory(IReadOnlyList<SessionEntry> compactedEntries) => Runtime.ReplaceHistory(compactedEntries);

    /// <summary>
    /// Atomically captures an immutable history snapshot together with the
    /// destructive-mutation version observed at snapshot time. Compactors must
    /// operate on the returned <see cref="HistorySnapshot"/> rather than on
    /// <see cref="Session"/>.<c>History</c> directly, then call
    /// <see cref="TryReplaceHistoryFromSnapshot"/> to apply the result safely
    /// under the runtime lock.
    /// </summary>
    public HistorySnapshot SnapshotHistoryForCompaction() => Runtime.SnapshotHistoryForCompaction();

    /// <summary>
    /// Optimistically applies a replacement history derived from an earlier
    /// snapshot. See <see cref="HistoryReplaceOutcome"/> for outcomes; see
    /// <see cref="GatewaySessionRuntime.TryReplaceHistoryFromSnapshot"/> for
    /// the full contract including the <paramref name="restoreUpdatedAtOnApplied"/>
    /// in-lock UpdatedAt restoration semantics.
    /// </summary>
    public HistoryReplaceOutcome TryReplaceHistoryFromSnapshot(
        IReadOnlyList<SessionEntry> replacement,
        long expectedDestructiveVersion,
        int expectedHistoryCount,
        DateTimeOffset? restoreUpdatedAtOnApplied = null)
        => Runtime.TryReplaceHistoryFromSnapshot(replacement, expectedDestructiveVersion, expectedHistoryCount, restoreUpdatedAtOnApplied);

    /// <summary>Returns a snapshot of the history (safe to iterate).</summary>
    public IReadOnlyList<SessionEntry> GetHistorySnapshot() => Runtime.GetHistorySnapshot();

    /// <summary>
    /// Returns a paginated snapshot of the history (safe to iterate).
    /// </summary>
    /// <param name="offset">Zero-based offset into history.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    public IReadOnlyList<SessionEntry> GetHistorySnapshot(int offset, int limit) => Runtime.GetHistorySnapshot(offset, limit);

    /// <summary>
    /// Wraps an existing domain <see cref="Session"/> record in a new
    /// <see cref="GatewaySession"/>. Used by tests and serialization paths that
    /// already hold a domain record and need the gateway-level lock + replay buffer.
    /// </summary>
    public static GatewaySession FromSession(Session session) => new(session);

    // Applies the injected redactor to sensitive fields before the entry is stored.
    private SessionEntry Redact(SessionEntry entry)
    {
        if (_redactor is null)
            return entry;

        var redactedContent = _redactor.Redact(entry.Content);
        var redactedArgs = entry.ToolArgs is null ? null : _redactor.Redact(entry.ToolArgs);

        if (ReferenceEquals(redactedContent, entry.Content) && ReferenceEquals(redactedArgs, entry.ToolArgs))
            return entry;

        return entry with { Content = redactedContent, ToolArgs = redactedArgs };
    }
}

/// <summary>
/// A single entry in the session conversation history.
/// </summary>
public sealed record SessionEntry
{
    /// <summary>Message role.</summary>
    public required MessageRole Role { get; init; }

    /// <summary>Message content.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Original content parts as received from the channel (before media processing).
    /// Null for text-only messages.
    /// </summary>
    public IReadOnlyList<MessageContentPart>? OriginalContentParts { get; init; }

    /// <summary>
    /// Content parts after media pipeline processing (e.g., audio → transcription).
    /// Null when no processing occurred.
    /// </summary>
    public IReadOnlyList<MessageContentPart>? ProcessedContentParts { get; init; }

    /// <summary>When this entry was recorded.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Optional proxy-trigger origin for this entry. <c>null</c> for normal
    /// channel-driven user turns (the default). Internal triggers stamp the
    /// kind of proxy they are: <see cref="TriggerType.Cron"/> for scheduled
    /// jobs (proxy for the citizen who scheduled them, per directive W-2),
    /// <see cref="TriggerType.Soul"/> for daily reflection ticks,
    /// <see cref="TriggerType.Heartbeat"/> for system liveness checks. The
    /// trigger lives on the entry — not on the session — because directives
    /// G-3/G-4 collapse <c>SessionType.Soul/Cron/Heartbeat</c> in P9-E; the
    /// session is just "what kind of conversation" (AgentSelf, UserAgent,
    /// AgentSubAgent, AgentAgent) and the trigger is per-turn metadata.
    /// </summary>
    public TriggerType? Trigger { get; init; }

    /// <summary>Tool name (when Role is <see cref="MessageRole.Tool"/>).</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool call ID for correlating requests and results.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Serialized JSON args for tool call (when Role is <see cref="MessageRole.Tool"/> and this is a ToolStart entry).</summary>
    public string? ToolArgs { get; init; }

    /// <summary>True if the tool call resulted in an error.</summary>
    public bool ToolIsError { get; init; }

    /// <summary>True if this entry is a compaction summary (not a real conversation message).</summary>
    public bool IsCompactionSummary { get; init; }

    /// <summary>
    /// True if this entry is a crash sentinel written before an agent turn begins.
    /// A sentinel that survives a gateway restart indicates the previous run was interrupted.
    /// Sentinels are removed on clean turn completion and must not be forwarded to the LLM.
    /// </summary>
    public bool IsCrashSentinel { get; init; }

    /// <summary>
    /// True if this entry has been folded into a later compaction summary and must NOT be
    /// projected into LLM context. Historical entries remain in the session store for
    /// transcript fidelity, replay, audit, and UI fold/collapse. Orthogonal to
    /// <see cref="IsCompactionSummary"/>: a compaction summary itself becomes
    /// <c>IsHistory = true</c> once a newer summary supersedes it, at which point only
    /// the most recent summary stays LLM-visible.
    /// </summary>
    public bool IsHistory { get; init; }

    /// <summary>
    /// Optional idempotency key for this user turn. When supplied by the sender
    /// (e.g. the cross-world relay path), the receiver checks whether the last
    /// user entry in the transcript already carries the same key before appending.
    /// Prevents duplicate user turns on cancel-and-retry. Null for all non-relay
    /// turns; ignored by the LLM context builder.
    /// </summary>
    public string? TurnIdempotencyKey { get; init; }

    /// <summary>
    /// Accumulated thinking/reasoning content from the model response.
    /// Null when the model did not produce reasoning blocks.
    /// </summary>
    public string? ThinkingContent { get; init; }

    /// <summary>
    /// Orthogonal, typed presentation/delivery kind for this transcript entry (issue #2149).
    /// <c>null</c> (the default) means the entry carries no explicit kind and is treated as
    /// <see cref="MessageKind.Message"/> - so legacy rows persisted before this field existed
    /// default safely on replay. The gateway stamps <see cref="MessageKind.SubAgentCompletion"/>
    /// on the inbound completion entry and <see cref="MessageKind.SubAgentResponse"/> on the
    /// parent agent's response entry produced while handling that completion, letting channels
    /// distinguish the three cases without re-parsing <see cref="Role"/>, ids, or content. Kept
    /// orthogonal to <see cref="Role"/>, which remains the LLM/conversation role.
    /// </summary>
    public MessageKind? Kind { get; init; }

    /// <summary>
    /// Resolves the effective <see cref="MessageKind"/> for this entry, mapping the unset
    /// <see cref="Kind"/> (including legacy rows) to <see cref="MessageKind.Message"/>.
    /// </summary>
    /// <returns>The stamped kind, or <see cref="MessageKind.Message"/> when none was supplied.</returns>
    public MessageKind ResolveKind() => Kind ?? MessageKind.Message;
}

/// <summary>
/// A sequenced outbound stream payload stored for reconnect replay. The
/// payload is transport-agnostic — channel adapters own delivery; this record
/// just timestamps and sequences the JSON body so a reconnecting channel can
/// replay any gap from the last acknowledged sequence ID.
/// </summary>
/// <param name="SequenceId">Monotonically increasing sequence ID.</param>
/// <param name="PayloadJson">Serialized outbound payload.</param>
/// <param name="Timestamp">When the payload was recorded.</param>
public sealed record GatewaySessionStreamEvent(long SequenceId, string PayloadJson, DateTimeOffset Timestamp);
