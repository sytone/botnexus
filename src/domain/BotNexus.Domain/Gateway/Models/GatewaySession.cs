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

    /// <summary>The agent this session is bound to.</summary>
    public AgentId AgentId
    {
        get => Session.AgentId;
        set => Session.AgentId = value;
    }

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
    /// Conversation this session belongs to, or <c>null</c> for orphan / legacy
    /// ungrouped sessions. Always mutate through this proxy — the
    /// <see cref="GatewaySessionFacadeArchitectureTests"/> fence bans reach-through
    /// access via <c>session.Session.ConversationId</c> so the proxy stays the
    /// single source of truth (F-9 / Phase 7).
    /// </summary>
    public ConversationId? ConversationId
    {
        get => Session.ConversationId;
        set => Session.ConversationId = value;
    }

    /// <summary>Computed interactivity marker.</summary>
    public bool IsInteractive => Session.IsInteractive;

    /// <summary>Participants in this session.</summary>
    public List<SessionParticipant> Participants
    {
        get => Session.Participants;
        init => Session.Participants = value;
    }

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

    /// <summary>Session-level metadata for extensibility.</summary>
    public Dictionary<string, object?> Metadata
    {
        get => Session.Metadata;
        init => Session.Metadata = value;
    }

    /// <summary>
    /// Next WebSocket outbound sequence ID for reconnect replay.
    /// </summary>
    public long NextSequenceId
    {
        get => Runtime.NextSequenceId;
        set => Runtime.NextSequenceId = value;
    }

    /// <summary>
    /// Bounded replay log of sequenced outbound WebSocket payloads.
    /// </summary>
    public List<GatewaySessionStreamEvent> StreamEventLog => Runtime.StreamEventLog;

    /// <summary>
    /// Replay buffer for outbound sequenced payloads.
    /// </summary>
    public SessionReplayBuffer ReplayBuffer => Runtime.ReplayBuffer;

    /// <summary>
    /// Removes any crash-sentinel entries from the session history.
    /// Call on clean turn completion to prevent sentinels from persisting after a successful run.
    /// </summary>
    public void RemoveCrashSentinels() => Runtime.RemoveCrashSentinels();

    /// <summary>Thread-safe append to conversation history. Content is redacted before storage when a redactor is configured.</summary>
    public void AddEntry(SessionEntry entry) => Runtime.AddEntry(Redact(entry));

    /// <summary>Thread-safe append of multiple entries. Content is redacted before storage when a redactor is configured.</summary>
    public void AddEntries(IEnumerable<SessionEntry> entries) => Runtime.AddEntries(entries.Select(Redact));

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
    /// the full contract.
    /// </summary>
    public HistoryReplaceOutcome TryReplaceHistoryFromSnapshot(
        IReadOnlyList<SessionEntry> replacement,
        long expectedDestructiveVersion,
        int expectedHistoryCount)
        => Runtime.TryReplaceHistoryFromSnapshot(replacement, expectedDestructiveVersion, expectedHistoryCount);

    /// <summary>Returns a snapshot of the history (safe to iterate).</summary>
    public IReadOnlyList<SessionEntry> GetHistorySnapshot() => Runtime.GetHistorySnapshot();

    /// <summary>
    /// Returns a paginated snapshot of the history (safe to iterate).
    /// </summary>
    /// <param name="offset">Zero-based offset into history.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    public IReadOnlyList<SessionEntry> GetHistorySnapshot(int offset, int limit) => Runtime.GetHistorySnapshot(offset, limit);

    /// <summary>
    /// Atomically allocates and returns the next outbound sequence ID.
    /// </summary>
    public long AllocateSequenceId() => Runtime.AllocateSequenceId();

    /// <summary>
    /// Records a sequenced outbound payload into the bounded replay log.
    /// </summary>
    public void AddStreamEvent(long sequenceId, string payloadJson, int replayWindowSize) => Runtime.AddStreamEvent(sequenceId, payloadJson, replayWindowSize);

    /// <summary>
    /// Returns replay entries after <paramref name="lastSequenceId"/>, bounded by <paramref name="maxReplayCount"/>.
    /// </summary>
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventsAfter(long lastSequenceId, int maxReplayCount)
        => Runtime.GetStreamEventsAfter(lastSequenceId, maxReplayCount);

    /// <summary>
    /// Returns a safe snapshot of replay entries.
    /// </summary>
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventSnapshot()
        => Runtime.GetStreamEventSnapshot();

    /// <summary>
    /// Replaces replay state from persisted storage.
    /// </summary>
    public void SetStreamReplayState(long nextSequenceId, IEnumerable<GatewaySessionStreamEvent>? streamEvents)
        => Runtime.SetStreamReplayState(nextSequenceId, streamEvents);

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
}

/// <summary>
/// A sequenced outbound WebSocket payload stored for reconnect replay.
/// </summary>
/// <param name="SequenceId">Monotonically increasing sequence ID.</param>
/// <param name="PayloadJson">Serialized outbound payload.</param>
/// <param name="Timestamp">When the payload was recorded.</param>
public sealed record GatewaySessionStreamEvent(long SequenceId, string PayloadJson, DateTimeOffset Timestamp);
