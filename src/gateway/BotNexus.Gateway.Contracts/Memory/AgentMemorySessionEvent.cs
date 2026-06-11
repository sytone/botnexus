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
public sealed record AgentMemorySessionEvent(
    string AgentId,
    string SessionId,
    string? ConversationId = null,
    DateTimeOffset? EndedAt = null,
    int TurnCount = 0);
