using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// TDD coverage for issue #2139: conversation metadata and binding updates must be transactional
/// so concurrent partial REST updates cannot clobber each other or revert unrelated fields.
///
/// Each test drives the <em>real</em> <see cref="ConversationsController"/> over the <em>real</em>
/// <see cref="SqliteConversationStore"/> (via a temp database) with a deterministic two-party
/// write barrier. The barrier holds every guarded mutation at its write phase until both
/// concurrent requests have finished their read phase, forcing the exact interleaving that made
/// the old whole-record <c>SaveAsync</c> path lose writes. With the transactional operations the
/// interleaving is safe.
/// </summary>
public sealed class ConversationTransactionalUpdateTests : IDisposable
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public ConversationTransactionalUpdateTests()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"botnexus-2139-{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_databasePath};Pooling=False";
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
    }

    private SqliteConversationStore CreateRealStore()
        => new(_connectionString, NullLogger<SqliteConversationStore>.Instance);

    private static ConversationsController CreateController(IConversationStore store)
        => new(store, new InMemorySessionStore());

    private static Conversation NewConversation(out ConversationId id)
    {
        id = ConversationId.Create();
        return new Conversation
        {
            ConversationId = id,
            AgentId = AgentId.From("agent-2139"),
            Title = "Transactional test",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static AddBindingRequest Binding(string address)
        => new(ChannelType: "signal", ChannelAddress: address, Mode: "Interactive", ThreadingMode: "Single", DisplayPrefix: null);

    // ── Test 1: two add-binding requests, both reads before either write, both bindings survive ──
    [Fact]
    public async Task TwoConcurrentAddBindings_BothSurvive()
    {
        var real = CreateRealStore();
        var conversation = NewConversation(out var id);
        await real.CreateAsync(conversation);

        var barrier = new TwoPartyWriteBarrier(real);
        var controller = CreateController(barrier);

        var a = controller.AddBinding(id.Value, Binding("addr-a"), CancellationToken.None);
        var b = controller.AddBinding(id.Value, Binding("addr-b"), CancellationToken.None);
        await Task.WhenAll(a, b);

        var loaded = await real.GetAsync(id);
        loaded.ShouldNotBeNull();
        loaded!.ChannelBindings.Select(x => x.ChannelAddress.Value).OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(["addr-a", "addr-b"]);
    }

    // ── Test 2: remove one binding while another is added; only the requested one is removed ──
    [Fact]
    public async Task RemoveOneBinding_WhileAddingAnother_OnlyRequestedRemoved()
    {
        var real = CreateRealStore();
        var conversation = NewConversation(out var id);
        var existing = new ChannelBinding
        {
            BindingId = BindingId.Create(),
            ChannelType = ChannelKey.From("signal"),
            ChannelAddress = ChannelAddress.From("addr-existing"),
            BoundAt = DateTimeOffset.UtcNow
        };
        conversation.ChannelBindings.Add(existing);
        await real.CreateAsync(conversation);

        var barrier = new TwoPartyWriteBarrier(real);
        var controller = CreateController(barrier);

        var remove = controller.RemoveBinding(id.Value, existing.BindingId.Value, CancellationToken.None);
        var add = controller.AddBinding(id.Value, Binding("addr-new"), CancellationToken.None);
        await Task.WhenAll(remove, add);

        var loaded = await real.GetAsync(id);
        loaded.ShouldNotBeNull();
        var addresses = loaded!.ChannelBindings.Select(x => x.ChannelAddress.Value).ToList();
        addresses.ShouldNotContain("addr-existing");
        addresses.ShouldContain("addr-new");
    }

    // ── Test 3: override update interleaved with PinAsync preserves both override and pin ──
    [Fact]
    public async Task OverrideUpdate_InterleavedWithPin_PreservesBoth()
    {
        var real = CreateRealStore();
        var conversation = NewConversation(out var id);
        await real.CreateAsync(conversation);

        var barrier = new TwoPartyWriteBarrier(real);
        var controller = CreateController(barrier);

        var setOverride = controller.SetOverride(
            id.Value,
            new SetConversationOverrideRequest(Model: "claude-opus-4", Thinking: "high", ContextWindow: 128_000),
            CancellationToken.None);
        // Pin goes through the store directly to model the independently-committed pin field.
        var pin = barrier.PinAsync(id, true);
        await Task.WhenAll(setOverride, pin);

        var loaded = await real.GetAsync(id);
        loaded.ShouldNotBeNull();
        loaded!.ModelOverride.ShouldBe("claude-opus-4");
        loaded.ThinkingOverride.ShouldBe("high");
        loaded.ContextWindowOverride.ShouldBe(128_000);
        loaded.IsPinned.ShouldBeTrue();
        loaded.PinnedAt.ShouldNotBeNull();
    }

    // ── Test 4: metadata patch interleaved with another narrow mutation preserves both ──
    [Fact]
    public async Task MetadataPatch_InterleavedWithPin_PreservesBoth()
    {
        var real = CreateRealStore();
        var conversation = NewConversation(out var id);
        await real.CreateAsync(conversation);

        var barrier = new TwoPartyWriteBarrier(real);
        var controller = CreateController(barrier);

        var patch = controller.Patch(
            id.Value,
            new PatchConversationRequest(Title: "Renamed", Purpose: "New purpose", Instructions: null),
            CancellationToken.None);
        var pin = barrier.PinAsync(id, true);
        await Task.WhenAll(patch, pin);

        var loaded = await real.GetAsync(id);
        loaded.ShouldNotBeNull();
        loaded!.Title.ShouldBe("Renamed");
        loaded.Purpose.ShouldBe("New purpose");
        loaded.IsPinned.ShouldBeTrue();
    }

    /// <summary>
    /// Decorator over a real <see cref="IConversationStore"/> that rendezvous the write phase of
    /// exactly two concurrent mutations. Each guarded write signals arrival and then blocks until
    /// the second write also arrives, guaranteeing both callers completed their read phase (the
    /// controller's <c>GetAsync</c>) before either write commits. Reads are passed straight
    /// through so the read phase is never gated.
    /// </summary>
    private sealed class TwoPartyWriteBarrier(IConversationStore inner) : IConversationStore
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _second = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        private async Task RendezvousAsync()
        {
            var n = Interlocked.Increment(ref _arrivals);
            if (n == 1)
            {
                _first.TrySetResult();
                await _second.Task.ConfigureAwait(false);
            }
            else
            {
                // Ensure the first arrival has recorded its arrival before we release the gate,
                // then wait for it too so both are past the read phase before either commits.
                await _first.Task.ConfigureAwait(false);
                _second.TrySetResult();
            }
        }

        // ── Guarded writes ──
        public async Task<bool> AddBindingAsync(ConversationId conversationId, ChannelBinding binding, CancellationToken ct = default)
        {
            await RendezvousAsync().ConfigureAwait(false);
            return await inner.AddBindingAsync(conversationId, binding, ct).ConfigureAwait(false);
        }

        public async Task<bool> RemoveBindingAsync(ConversationId conversationId, BindingId bindingId, CancellationToken ct = default)
        {
            await RendezvousAsync().ConfigureAwait(false);
            return await inner.RemoveBindingAsync(conversationId, bindingId, ct).ConfigureAwait(false);
        }

        public async Task<Conversation?> PatchMetadataAsync(ConversationId conversationId, ConversationMetadataPatch patch, CancellationToken ct = default)
        {
            await RendezvousAsync().ConfigureAwait(false);
            return await inner.PatchMetadataAsync(conversationId, patch, ct).ConfigureAwait(false);
        }

        public async Task<Conversation?> PatchOverrideAsync(ConversationId conversationId, ConversationOverridePatch patch, CancellationToken ct = default)
        {
            await RendezvousAsync().ConfigureAwait(false);
            return await inner.PatchOverrideAsync(conversationId, patch, ct).ConfigureAwait(false);
        }

        public async Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default)
        {
            await RendezvousAsync().ConfigureAwait(false);
            await inner.PinAsync(conversationId, pin, ct).ConfigureAwait(false);
        }

        // ── Pass-through ──
        public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default) => inner.GetAsync(conversationId, ct);
        public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default) => inner.ListAsync(agentId, ct);
        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default) => inner.ListForCitizenAsync(citizen, ct);
        public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default) => inner.AddParticipantsAsync(conversationId, participants, ct);
        public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default) => inner.CreateAsync(conversation, ct);
        public Task SaveAsync(Conversation conversation, CancellationToken ct = default) => inner.SaveAsync(conversation, ct);
        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default) => inner.ArchiveAsync(conversationId, ct);
        public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default) => inner.TouchAsync(conversationId, ct);
        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default) => inner.ResolveByBindingAsync(agentId, channelType, channelAddress, ct);
        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default) => inner.GetSummariesAsync(ct);
        public Task<IReadOnlyList<PendingAskUserCheckpoint>> GetPendingAskUserCheckpointsAsync(CancellationToken ct = default) => inner.GetPendingAskUserCheckpointsAsync(ct);
        public Task<bool> MoveBindingAsync(ConversationId fromConversationId, ConversationId toConversationId, BindingId bindingId, CancellationToken ct = default) => inner.MoveBindingAsync(fromConversationId, toConversationId, bindingId, ct);
        public Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default) => inner.GetCanvasStateAsync(conversationId, ct);
        public Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default) => inner.SetCanvasStateKeyAsync(conversationId, key, value, ct);
        public Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default) => inner.DeleteCanvasStateKeyAsync(conversationId, key, ct);
        public Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default) => inner.ClearCanvasStateAsync(conversationId, ct);
    }
}
