using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Conversations;

/// <summary>Thread-safe non-durable reference implementation of conversation read-state semantics.</summary>
public sealed class InMemoryConversationReadStateStore : IConversationReadStateStore
{
    private readonly ConcurrentDictionary<Key, ConversationReadState> _states = new();

    /// <inheritdoc />
    public Task<ConversationReadState?> GetAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = CreateKey(worldId, readerId, conversationId);
        _states.TryGetValue(key, out var state);
        return Task.FromResult(state);
    }

    /// <inheritdoc />
    public Task<ConversationReadState> AdvanceAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        ConversationReadPosition position,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = CreateKey(worldId, readerId, conversationId);
        var state = _states.AddOrUpdate(
            key,
            static (candidate, requested) => candidate.ToState(requested, 1),
            static (_, current, requested) => requested.Value > current.Position.Value
                ? current with { Position = requested, Version = checked(current.Version + 1) }
                : current,
            position);
        return Task.FromResult(state);
    }

    private static Key CreateKey(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId)
    {
        ConversationReadStateKey.Validate(readerId, conversationId);
        return new Key(ConversationReadStateKey.NormalizeWorldId(worldId), readerId, conversationId);
    }

    private readonly record struct Key(
        string WorldId,
        ConversationReaderId ReaderId,
        ConversationId ConversationId)
    {
        public ConversationReadState ToState(ConversationReadPosition position, long version)
            => new(WorldId, ReaderId, ConversationId, position, version);
    }
}
