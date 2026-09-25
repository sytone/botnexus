using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// The latest durable read cursor for one world, reader, and conversation. Version changes only
/// when the position advances, making retries observable as idempotent no-ops.
/// </summary>
public sealed record ConversationReadState(
    string WorldId,
    ConversationReaderId ReaderId,
    ConversationId ConversationId,
    ConversationReadPosition Position,
    long Version);

/// <summary>
/// Persists independently scoped conversation read cursors. Implementations must atomically retain
/// the greatest submitted position and must not increment the version for equal or lower retries.
/// </summary>
public interface IConversationReadStateStore
{
    /// <summary>Returns the current cursor for an exact world-reader-conversation key, if present.</summary>
    Task<ConversationReadState?> GetAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically advances an exact key to <paramref name="position"/> when it is greater than the
    /// stored position, returning the winning state after the operation.
    /// </summary>
    Task<ConversationReadState> AdvanceAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        ConversationReadPosition position,
        CancellationToken cancellationToken = default);
}

internal static class ConversationReadStateKey
{
    public static string NormalizeWorldId(string worldId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldId);
        return worldId.Trim();
    }

    public static void Validate(
        ConversationReaderId readerId,
        ConversationId conversationId)
    {
        if (!readerId.IsInitialized())
            throw new ArgumentException("Reader id must be initialized.", nameof(readerId));
        if (!conversationId.IsInitialized())
            throw new ArgumentException("Conversation id must be initialized.", nameof(conversationId));
    }
}
