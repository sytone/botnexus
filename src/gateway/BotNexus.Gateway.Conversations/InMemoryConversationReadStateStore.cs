using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Conversations;

/// <summary>Thread-safe non-durable reference implementation of conversation read-state semantics.</summary>
public sealed class InMemoryConversationReadStateStore : IConversationReadStateStore
{
    private readonly ConcurrentDictionary<Key, ConversationReadState> _states = new();
    private readonly object _advanceGate = new();

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
    public async Task<ConversationReadState> AdvanceAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        ConversationReadPosition position,
        CancellationToken cancellationToken = default)
        => (await AdvanceWithResultAsync(worldId, readerId, conversationId, position, cancellationToken)
            .ConfigureAwait(false)).State;

    /// <inheritdoc />
    public Task<ConversationReadStateAdvanceResult> AdvanceWithResultAsync(
        string worldId,
        ConversationReaderId readerId,
        ConversationId conversationId,
        ConversationReadPosition position,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = CreateKey(worldId, readerId, conversationId);
        lock (_advanceGate)
        {
            if (!_states.TryGetValue(key, out var current))
            {
                var initial = key.ToState(position, 1);
                _states[key] = initial;
                return Task.FromResult(new ConversationReadStateAdvanceResult(initial, true));
            }

            if (position.Value <= current.Position.Value)
                return Task.FromResult(new ConversationReadStateAdvanceResult(current, false));

            var next = current with { Position = position, Version = checked(current.Version + 1) };
            _states[key] = next;
            return Task.FromResult(new ConversationReadStateAdvanceResult(next, true));
        }
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
