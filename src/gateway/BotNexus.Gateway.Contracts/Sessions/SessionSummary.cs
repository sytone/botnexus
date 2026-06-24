using BotNexus.Gateway.Abstractions.Models;
using ChannelKey = BotNexus.Domain.Primitives.ChannelKey;
using SessionType = BotNexus.Domain.Primitives.SessionType;

namespace BotNexus.Gateway.Abstractions.Sessions;

/// <summary>
/// Represents session summary.
/// </summary>
public sealed record SessionSummary(
    string SessionId,
    string AgentId,
    ChannelKey? ChannelType,
    SessionStatus Status,
    SessionType SessionType,
    bool IsInteractive,
    int MessageCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ConversationId = null)
{
    /// <summary>
    /// Projects a fully materialised <see cref="GatewaySession"/> down to a lightweight
    /// summary. Used by session stores that have no transcript-free read path (File,
    /// InMemory, test doubles) so they can satisfy
    /// <see cref="ISessionStore.ListSummariesAsync"/> from the same data they already load.
    /// The SQLite store builds summaries directly from a metadata-only query instead.
    /// </summary>
    public static SessionSummary FromSession(GatewaySession session) => new(
        session.SessionId.Value,
        session.AgentId.Value,
        session.ChannelType,
        session.Status,
        session.SessionType,
        session.IsInteractive,
        session.MessageCount,
        session.CreatedAt,
        session.UpdatedAt,
        session.ConversationId.IsInitialized() ? session.ConversationId.Value : null);
}
