using BotNexus.Domain.Primitives;

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

    public GatewaySession()
        : this(new Session())
    {
    }

    public GatewaySession(Session session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
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

    /// <summary>Thread-safe append to conversation history.</summary>
    public void AddEntry(SessionEntry entry) => Runtime.AddEntry(entry);

    /// <summary>Thread-safe append of multiple entries.</summary>
    public void AddEntries(IEnumerable<SessionEntry> entries) => Runtime.AddEntries(entries);

    /// <summary>Replaces the session history with a compacted version.</summary>
    public void ReplaceHistory(IReadOnlyList<SessionEntry> compactedEntries) => Runtime.ReplaceHistory(compactedEntries);

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

    public Session ToSession() => Session;

    public static GatewaySession FromSession(Session session) => new(session);
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

    /// <summary>When this entry was recorded.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Tool name (when Role is <see cref="MessageRole.Tool"/>).</summary>
    public string? ToolName { get; init; }

    /// <summary>Tool call ID for correlating requests and results.</summary>
    public string? ToolCallId { get; init; }

    /// <summary>True if this entry is a compaction summary (not a real conversation message).</summary>
    public bool IsCompactionSummary { get; init; }
}

/// <summary>
/// A sequenced outbound WebSocket payload stored for reconnect replay.
/// </summary>
/// <param name="SequenceId">Monotonically increasing sequence ID.</param>
/// <param name="PayloadJson">Serialized outbound payload.</param>
/// <param name="Timestamp">When the payload was recorded.</param>
public sealed record GatewaySessionStreamEvent(long SequenceId, string PayloadJson, DateTimeOffset Timestamp);
