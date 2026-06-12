namespace BotNexus.Gateway.Contracts.Memory;

/// <summary>
/// Event raised when a session completes, allowing memory providers to
/// flush pending writes or perform session-scoped bookkeeping.
/// </summary>
/// <param name="AgentId">The agent that owned the session.</param>
/// <param name="SessionId">The session that completed.</param>
/// <param name="ConversationId">The conversation the session belonged to, if any.</param>
/// <param name="EndedAt">When the session ended.</param>
/// <param name="TurnCount">Total number of turns in the session.</param>
/// <param name="History">The session history entries for indexing. May be empty if not available.</param>
public sealed record AgentMemorySessionEvent(
    string AgentId,
    string SessionId,
    string? ConversationId = null,
    DateTimeOffset? EndedAt = null,
    int TurnCount = 0,
    IReadOnlyList<AgentMemorySessionTurn>? History = null);

/// <summary>
/// A single turn entry from a completed session, carrying the minimal data needed for memory indexing.
/// </summary>
/// <param name="Index">Position in the session history.</param>
/// <param name="Role">The message role (user, assistant, tool, system).</param>
/// <param name="Content">The text content of the turn.</param>
/// <param name="Timestamp">When this turn was produced.</param>
public sealed record AgentMemorySessionTurn(
    int Index,
    string Role,
    string? Content,
    DateTimeOffset Timestamp);
