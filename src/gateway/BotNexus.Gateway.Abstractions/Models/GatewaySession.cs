namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A gateway-level session that tracks an interaction between a caller and an agent.
/// Sessions own the conversation history and bridge the Gateway with the underlying
/// <c>AgentCore.Agent</c> execution.
/// </summary>
public sealed class GatewaySession
{
    private readonly Lock _historyLock = new();
    private readonly SessionReplayBuffer _replayBuffer = new();

    /// <summary>Unique session identifier.</summary>
    public required string SessionId { get; init; }

    /// <summary>The agent this session is bound to.</summary>
    public required string AgentId { get; set; }

    /// <summary>The channel this session originated from (e.g., "signalr", "telegram").</summary>
    public string? ChannelType { get; set; }

    /// <summary>Caller-specific identifier within the channel (e.g., user ID, chat ID).</summary>
    public string? CallerId { get; set; }

    /// <summary>When the session was created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>When the session was last active.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Current lifecycle status of the session.</summary>
    public SessionStatus Status { get; set; } = SessionStatus.Active;

    /// <summary>Timestamp when this session expires, if known.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// Conversation history as serializable entries.
    /// This is the Gateway-level view; the AgentCore maintains its own message timeline.
    /// </summary>
    public List<SessionEntry> History { get; init; } = [];

    /// <summary>Number of entries in the conversation history.</summary>
    public int MessageCount => History.Count;

    /// <summary>Session-level metadata for extensibility.</summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];

    /// <summary>
    /// Next WebSocket outbound sequence ID for reconnect replay.
    /// </summary>
    public long NextSequenceId
    {
        get => _replayBuffer.NextSequenceId;
        set => _replayBuffer.NextSequenceId = value;
    }

    /// <summary>
    /// Bounded replay log of sequenced outbound WebSocket payloads.
    /// </summary>
    public List<GatewaySessionStreamEvent> StreamEventLog => [.. _replayBuffer.GetStreamEventSnapshot()];

    /// <summary>
    /// Replay buffer for outbound sequenced payloads.
    /// </summary>
    public SessionReplayBuffer ReplayBuffer => _replayBuffer;

    /// <summary>Thread-safe append to conversation history.</summary>
    public void AddEntry(SessionEntry entry)
    {
        lock (_historyLock)
        {
            History.Add(entry);
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Thread-safe append of multiple entries.</summary>
    public void AddEntries(IEnumerable<SessionEntry> entries)
    {
        lock (_historyLock)
        {
            History.AddRange(entries);
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Replaces the session history with a compacted version.</summary>
    public void ReplaceHistory(IReadOnlyList<SessionEntry> compactedEntries)
    {
        lock (_historyLock)
        {
            History.Clear();
            History.AddRange(compactedEntries);
            UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Returns a snapshot of the history (safe to iterate).</summary>
    public IReadOnlyList<SessionEntry> GetHistorySnapshot()
    {
        lock (_historyLock)
        {
            return History.ToList();
        }
    }

    /// <summary>
    /// Returns a paginated snapshot of the history (safe to iterate).
    /// </summary>
    /// <param name="offset">Zero-based offset into history.</param>
    /// <param name="limit">Maximum number of items to return.</param>
    public IReadOnlyList<SessionEntry> GetHistorySnapshot(int offset, int limit)
    {
        lock (_historyLock)
        {
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be greater than or equal to zero.");
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than or equal to zero.");

            return History
                .Skip(offset)
                .Take(limit)
                .ToList();
        }
    }

    /// <summary>
    /// Atomically allocates and returns the next outbound sequence ID.
    /// </summary>
    public long AllocateSequenceId()
    {
        var sequenceId = _replayBuffer.AllocateSequenceId();
        UpdatedAt = DateTimeOffset.UtcNow;
        return sequenceId;
    }

    /// <summary>
    /// Records a sequenced outbound payload into the bounded replay log.
    /// </summary>
    public void AddStreamEvent(long sequenceId, string payloadJson, int replayWindowSize)
    {
        _replayBuffer.AddStreamEvent(sequenceId, payloadJson, replayWindowSize);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Returns replay entries after <paramref name="lastSequenceId"/>, bounded by <paramref name="maxReplayCount"/>.
    /// </summary>
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventsAfter(long lastSequenceId, int maxReplayCount)
        => _replayBuffer.GetStreamEventsAfter(lastSequenceId, maxReplayCount);

    /// <summary>
    /// Returns a safe snapshot of replay entries.
    /// </summary>
    public IReadOnlyList<GatewaySessionStreamEvent> GetStreamEventSnapshot()
        => _replayBuffer.GetStreamEventSnapshot();

    /// <summary>
    /// Replaces replay state from persisted storage.
    /// </summary>
    public void SetStreamReplayState(long nextSequenceId, IEnumerable<GatewaySessionStreamEvent>? streamEvents)
        => _replayBuffer.SetState(nextSequenceId, streamEvents);
}

/// <summary>
/// A single entry in the session conversation history.
/// </summary>
public sealed record SessionEntry
{
    /// <summary>Message role: "user", "assistant", "system", or "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Message content.</summary>
    public required string Content { get; init; }

    /// <summary>When this entry was recorded.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Tool name (when Role is "tool").</summary>
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
