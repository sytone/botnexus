using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Conversations;

/// <summary>
/// In-memory conversation store for development and testing.
/// Not durable — all conversations are lost on restart.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
        => Task.FromResult(_conversations.GetValueOrDefault(conversationId.Value));

    /// <inheritdoc />
    public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        IReadOnlyList<Conversation> results = agentId is null
            ? [.. _conversations.Values]
            : [.. _conversations.Values.Where(c => c.AgentId == agentId.Value)];
        return Task.FromResult(results);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
    {
        if (!citizen.IsValid)
            throw new ArgumentException("Citizen must be a valid (non-default) CitizenId.", nameof(citizen));

        IReadOnlyList<Conversation> results = [.. _conversations.Values.Where(c => MatchesCitizen(c, citizen))];
        return Task.FromResult(results);
    }

    private static bool MatchesCitizen(Conversation conversation, CitizenId citizen)
    {
        if (conversation.Initiator is { IsValid: true } init && init == citizen)
            return true;

        // Owner-match: only agent-species citizens own conversations.
        if (citizen.Kind == CitizenKind.Agent && citizen.AsAgent is { } agent && conversation.AgentId == agent)
            return true;

        return false;
    }

    public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
    {
        if (!_conversations.TryAdd(conversation.ConversationId.Value, conversation))
            throw new InvalidOperationException($"A conversation with id '{conversation.ConversationId}' already exists.");
        return Task.FromResult(conversation);
    }

    /// <inheritdoc />
    public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        conversation = conversation with { UpdatedAt = DateTimeOffset.UtcNow };
        _conversations[conversation.ConversationId.Value] = conversation;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        if (_conversations.TryGetValue(conversationId.Value, out var existing))
            _conversations[conversationId.Value] = existing with
            {
                Status = ConversationStatus.Archived,
                ActiveSessionId = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Conversation?> ResolveByBindingAsync(
        AgentId agentId,
        ChannelKey channelType,
        ChannelAddress channelAddress,
        CancellationToken ct = default)
    {
        var match = _conversations.Values.FirstOrDefault(c =>
            c.AgentId == agentId &&
            c.Status == ConversationStatus.Active &&
            c.ChannelBindings.Any(b =>
                b.ChannelType == channelType &&
                b.ChannelAddress == channelAddress));

        return Task.FromResult(match);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        var source = agentId is null
            ? _conversations.Values
            : _conversations.Values.Where(c => c.AgentId == agentId.Value);

        // Archived conversations are excluded from the active list.
        IReadOnlyList<ConversationSummary> summaries = [.. source
            .Where(c => c.Status != ConversationStatus.Archived)
            .Select(ToSummary)];
        return Task.FromResult(summaries);
    }

    private static ConversationSummary ToSummary(Conversation c) =>
        new(
            c.ConversationId.Value,
            c.AgentId.Value,
            c.Title,
            c.IsDefault,
            c.Status.ToString(),
            c.ActiveSessionId?.Value,
            c.ChannelBindings.Count,
            c.CreatedAt,
            c.UpdatedAt,
            c.Purpose);
}
