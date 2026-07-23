using System.Collections.Concurrent;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Configuration;
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _participantLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, JsonElement>> _canvasState = new(StringComparer.Ordinal);
    private readonly IWorldContext? _worldContext;

    /// <summary>Initialises a new <see cref="InMemoryConversationStore"/> without world stamping.</summary>
    /// <remarks>
    /// Kept for tests and bare wire-ups that don't have a world context available; production
    /// callers should always provide <see cref="IWorldContext"/> via the world-aware overload so
    /// <c>Conversation.WorldId</c> is stamped on persistence.
    /// </remarks>
    public InMemoryConversationStore() { }

    /// <summary>Initialises a new <see cref="InMemoryConversationStore"/> that stamps the current world id.</summary>
    /// <param name="worldContext">Resolves the gateway's current <see cref="WorldIdentity"/> for stamping.</param>
    public InMemoryConversationStore(IWorldContext worldContext)
    {
        _worldContext = worldContext;
    }

    /// <inheritdoc />
    public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        var conversation = _conversations.GetValueOrDefault(conversationId.Value);
        return Task.FromResult(BackfillWorldId(conversation));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        IReadOnlyList<Conversation> results = agentId is null
            ? [.. _conversations.Values.Select(c => BackfillWorldId(c)!)]
            : [.. _conversations.Values.Where(c => c.AgentId == agentId.Value).Select(c => BackfillWorldId(c)!)];
        return Task.FromResult(results);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
    {
        if (!citizen.IsValid)
            throw new ArgumentException("Citizen must be a valid (non-default) CitizenId.", nameof(citizen));

        IReadOnlyList<Conversation> results = [.. _conversations.Values
            .Where(c => MatchesCitizen(c, citizen))
            .Select(c => BackfillWorldId(c)!)];
        return Task.FromResult(results);
    }

    // Citizen scoping is shared across all three conversation stores — see ConversationStoreShared (#1383).
    private static bool MatchesCitizen(Conversation conversation, CitizenId citizen)
        => ConversationStoreShared.MatchesCitizen(conversation, citizen);

    public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
    {
        StampWorldId(conversation);
        if (!_conversations.TryAdd(conversation.ConversationId.Value, conversation))
            throw new InvalidOperationException($"A conversation with id '{conversation.ConversationId}' already exists.");
        return Task.FromResult(conversation);
    }

    /// <inheritdoc />
    public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        if (conversation.Status == ConversationStatus.Archived && conversation.ActiveSessionId is not null)
            throw new InvalidOperationException($"Conversation '{conversation.ConversationId}' cannot be archived while an active session is assigned.");
        StampWorldId(conversation);
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
    public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        if (_conversations.TryGetValue(conversationId.Value, out var existing))
            _conversations[conversationId.Value] = existing with { UpdatedAt = DateTimeOffset.UtcNow };
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default)
    {
        if (_conversations.TryGetValue(conversationId.Value, out var existing))
        {
            _conversations[conversationId.Value] = existing with
            {
                IsPinned = pin,
                PinnedAt = pin ? DateTimeOffset.UtcNow : null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task AddParticipantsAsync(
        ConversationId conversationId,
        IEnumerable<SessionParticipant> participants,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var snapshot = participants as IReadOnlyCollection<SessionParticipant> ?? participants.ToArray();
        if (snapshot.Count == 0)
            return;

        var conversationLock = GetParticipantLock(conversationId.Value);
        await conversationLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_conversations.TryGetValue(conversationId.Value, out var existing))
                return;

            var byCitizen = new Dictionary<CitizenId, SessionParticipant>(existing.Participants.Count);
            foreach (var p in existing.Participants)
                byCitizen[p.CitizenId] = p;

            foreach (var participant in snapshot)
            {
                if (!participant.CitizenId.IsValid)
                    continue;
                // First-add wins on role to match the SQLite semantics.
                if (!byCitizen.ContainsKey(participant.CitizenId))
                    byCitizen[participant.CitizenId] = new SessionParticipant
                    {
                        CitizenId = participant.CitizenId,
                        Role = participant.Role
                    };
            }

            existing.Participants = byCitizen.Values.ToList();
        }
        finally
        {
            conversationLock.Release();
        }
    }

    private SemaphoreSlim GetParticipantLock(string conversationId)
        => _participantLocks.GetOrAdd(conversationId, static _ => new SemaphoreSlim(1, 1));

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

        return Task.FromResult(BackfillWorldId(match));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        // Archived conversations are excluded from the active list.
        IReadOnlyList<ConversationSummary> summaries = [.. _conversations.Values
            .Where(c => c.Status != ConversationStatus.Archived)
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.PinnedAt)
            .ThenByDescending(c => c.UpdatedAt)
            .ThenBy(c => c.ConversationId.Value, StringComparer.Ordinal)
            .Select(ToSummary)];
        return Task.FromResult(summaries);
    }

    // World-id stamping/back-fill is shared across all three conversation stores — see
    // ConversationStoreShared (#1383). These forwarders thread this store's world context
    // into the shared logic while keeping the existing call-site signatures unchanged.
    private void StampWorldId(Conversation conversation)
        => ConversationStoreShared.StampWorldId(conversation, _worldContext);

    private Conversation? BackfillWorldId(Conversation? conversation)
        => ConversationStoreShared.BackfillWorldId(conversation, _worldContext);

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
            c.Purpose,
            c.Kind.ToString(),
            c.IsPinned,
            c.PinnedAt,
            c.Participants.Select(p => new ParticipantSummary(
                p.CitizenId.Kind.ToString(),
                p.CitizenId.Value,
                p.Role)).ToList());

    // ── Canvas State ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        if (!_conversations.ContainsKey(conversationId.Value))
            return Task.FromResult<Dictionary<string, JsonElement>?>(null);

        if (_canvasState.TryGetValue(conversationId.Value, out var state))
            return Task.FromResult<Dictionary<string, JsonElement>?>(new Dictionary<string, JsonElement>(state));

        return Task.FromResult<Dictionary<string, JsonElement>?>(new Dictionary<string, JsonElement>());
    }

    /// <inheritdoc />
    public Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default)
    {
        if (!_conversations.ContainsKey(conversationId.Value))
            return Task.FromResult(false);

        var state = _canvasState.GetOrAdd(conversationId.Value, _ => new ConcurrentDictionary<string, JsonElement>(StringComparer.Ordinal));
        state[key] = value;
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default)
    {
        if (_canvasState.TryGetValue(conversationId.Value, out var state))
            state.TryRemove(key, out _);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
    {
        _canvasState.TryRemove(conversationId.Value, out _);
        return Task.CompletedTask;
    }
}

