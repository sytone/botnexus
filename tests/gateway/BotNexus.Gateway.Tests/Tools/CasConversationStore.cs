using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;

namespace BotNexus.Gateway.Tests.Tools;

/// <summary>
/// An <see cref="IConversationStore"/> decorator that reproduces the SQLite store's
/// compare-and-swap save guard (#2471) on top of the in-memory store, plus deterministic
/// interleaving hooks so a concurrent writer can be injected at an exact point.
/// </summary>
/// <remarks>
/// Used by <see cref="ConversationSaveRetryTests"/>. Interleaving is executed inline inside
/// <see cref="SaveAsync"/> rather than on a background thread, so the tests are deterministic
/// and need no sleeps: the "other writer" provably commits between the tool's read and its save.
/// </remarks>
internal sealed class CasConversationStore : IConversationStore
{
    private readonly InMemoryConversationStore _inner = new();
    private readonly Dictionary<string, long> _versions = new(StringComparer.Ordinal);
    private Func<CasConversationStore, Task>? _interleave;
    private bool _interleaveRepeats;
    private Action<Conversation>? _onSaved;

    /// <summary>Gets the number of <see cref="SaveAsync"/> calls made, including retries.</summary>
    public int SaveAttempts { get; private set; }

    /// <summary>Runs <paramref name="writer"/> exactly once, before the next save is evaluated.</summary>
    public void InterleaveOnce(Func<CasConversationStore, Task> writer)
    {
        _interleave = writer;
        _interleaveRepeats = false;
    }

    /// <summary>Runs <paramref name="writer"/> before every save, so no attempt can ever win.</summary>
    public void InterleaveAlways(Func<CasConversationStore, Task> writer)
    {
        _interleave = writer;
        _interleaveRepeats = true;
    }

    /// <summary>Registers a callback fired after each successful save, for test gating.</summary>
    public void OnSaved(Action<Conversation> callback) => _onSaved = callback;

    /// <summary>Saves bypassing the CAS check, so a test's "other writer" can commit freely.</summary>
    public async Task ForceSaveAsync(Conversation conversation)
    {
        var key = conversation.ConversationId.Value;
        _versions[key] = Version(key) + 1;
        await _inner.SaveAsync(conversation with { Version = _versions[key] });
    }

    public async Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        SaveAttempts++;

        var interleave = _interleave;
        if (interleave is not null)
        {
            if (!_interleaveRepeats)
                _interleave = null;

            await interleave(this);
        }

        var key = conversation.ConversationId.Value;
        var committed = Version(key);
        if (conversation.Version != committed)
            throw new ConversationConcurrencyException(key, conversation.Version, committed);

        _versions[key] = committed + 1;
        var saved = conversation with { Version = committed + 1 };
        await _inner.SaveAsync(saved, ct);
        _onSaved?.Invoke(saved);
    }

    public async Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
    {
        var key = conversation.ConversationId.Value;
        _versions[key] = 1;
        return await _inner.CreateAsync(conversation with { Version = 1 }, ct);
    }

    public async Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default)
    {
        // Mirrors the real narrow patch operations: bumps the version without round-tripping the
        // aggregate, which is exactly what makes an in-flight SaveAsync go stale.
        await _inner.PinAsync(conversationId, pin, ct);
        var key = conversationId.Value;
        _versions[key] = Version(key) + 1;
        var current = await _inner.GetAsync(conversationId, ct);
        if (current is not null)
            await _inner.SaveAsync(current with { Version = _versions[key] }, ct);
    }

    private long Version(string key) => _versions.TryGetValue(key, out var v) ? v : 0;

    // ── Straight pass-through for everything the retry policy does not touch ──

    public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
        => _inner.GetAsync(conversationId, ct);

    public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
        => _inner.ListAsync(agentId, ct);

    public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
        => _inner.ListForCitizenAsync(citizen, ct);

    public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default)
        => _inner.AddParticipantsAsync(conversationId, participants, ct);

    public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
        => _inner.ArchiveAsync(conversationId, ct);

    public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
        => _inner.ResolveByBindingAsync(agentId, channelType, channelAddress, ct);

    public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default)
        => _inner.TouchAsync(conversationId, ct);

    public Task<bool> AddBindingAsync(ConversationId conversationId, ChannelBinding binding, CancellationToken ct = default)
        => _inner.AddBindingAsync(conversationId, binding, ct);

    public Task<bool> RemoveBindingAsync(ConversationId conversationId, BindingId bindingId, CancellationToken ct = default)
        => _inner.RemoveBindingAsync(conversationId, bindingId, ct);

    public Task<bool> MoveBindingAsync(ConversationId fromConversationId, ConversationId toConversationId, BindingId bindingId, CancellationToken ct = default)
        => _inner.MoveBindingAsync(fromConversationId, toConversationId, bindingId, ct);

    public Task<Conversation?> PatchMetadataAsync(ConversationId conversationId, ConversationMetadataPatch patch, CancellationToken ct = default)
        => _inner.PatchMetadataAsync(conversationId, patch, ct);

    public Task<Conversation?> PatchOverrideAsync(ConversationId conversationId, ConversationOverridePatch patch, CancellationToken ct = default)
        => _inner.PatchOverrideAsync(conversationId, patch, ct);

    public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
        => _inner.GetSummariesAsync(ct);

    public Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
        => _inner.GetCanvasStateAsync(conversationId, ct);

    public Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default)
        => _inner.SetCanvasStateKeyAsync(conversationId, key, value, ct);

    public Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default)
        => _inner.DeleteCanvasStateKeyAsync(conversationId, key, ct);

    public Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
        => _inner.ClearCanvasStateAsync(conversationId, ct);
}
