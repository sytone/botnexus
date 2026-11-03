using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>Exercises the Activity fallback through a store with only the legacy summary contract.</summary>
public sealed class ConversationStoreDefaultActivityTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("created")]
    [InlineData("updated")]
    [InlineData("inbound")]
    [InlineData("outbound")]
    public async Task ActivityFallback_UsesEffectiveActivityAndPreservesLegacySummaries(string signal)
    {
        var inner = new InMemoryConversationStore();
        var decorator = new LegacyStore(inner);
        IConversationStore store = decorator;
        var recent = Make("recent") with
        {
            CreatedAt = signal == "created" ? Cutoff : Cutoff.AddDays(-2),
            UpdatedAt = signal == "updated" ? Cutoff : Cutoff.AddDays(-2),
            Title = "Read-only work", Purpose = "Inspect the result", IsDefault = true,
            ActiveSessionId = SessionId.From("active-session"),
            Kind = ConversationKind.AgentAgent, Source = ConversationSource.Webhook,
            SourceId = "webhook-origin", Visibility = ConversationVisibility.InspectableReadOnly,
            IsPinned = true, PinnedAt = Cutoff.AddDays(-1)
        };
        recent.ChannelBindings.Add(new ChannelBinding
        {
            ChannelType = ChannelKey.From("signalr"), ChannelAddress = ChannelAddress.From("test"),
            LastInboundAt = signal == "inbound" ? Cutoff.ToOffset(TimeSpan.FromHours(-7)) : null,
            LastOutboundAt = signal == "outbound" ? Cutoff : null
        });
        recent.Participants.Add(new SessionParticipant { CitizenId = CitizenId.Of(AgentId.From("peer")), Role = "responder" });
        await inner.CreateAsync(recent);
        await inner.CreateAsync(Make("outside"));
        await inner.CreateAsync(Make("archived") with { Status = ConversationStatus.Archived, UpdatedAt = Cutoff });
        await inner.CreateAsync(Make("nested") with { ParentConversationId = ConversationId.From("parent"), UpdatedAt = Cutoff });

        var summary = (await store.GetSummariesAsync(Cutoff)).ShouldHaveSingleItem();
        (summary with { Participants = null }).ShouldBe(new ConversationSummary(
            "recent", "owner", "Read-only work", true, "Active", "active-session", 1,
            recent.CreatedAt, Cutoff, "Inspect the result", "AgentAgent", "Webhook",
            "InspectableReadOnly", true, recent.PinnedAt, SourceId: "webhook-origin"));
        summary.Participants.ShouldNotBeNull();
        summary.Participants.ShouldHaveSingleItem().ShouldBe(new ParticipantSummary("Agent", "peer", "responder"));
        decorator.LegacySummaryCalls.ShouldBe(0);

        var unbounded = await store.GetSummariesAsync((DateTimeOffset?)null);
        unbounded.Select(s => s.ConversationId).ShouldBe(new[] { "recent", "outside" });
        unbounded.Single(s => s.ConversationId == "recent").UpdatedAt.ShouldBe(Cutoff);
        unbounded.Single(s => s.ConversationId == "outside").UpdatedAt.ShouldBe(Cutoff.AddTicks(-1));
        var legacy = await store.GetSummariesAsync();
        legacy.ShouldContain(s => s.ConversationId == "outside");
        legacy.Single(s => s.ConversationId == "recent").UpdatedAt.ShouldBe(recent.UpdatedAt);
        recent.UpdatedAt.ShouldBe(signal == "updated" ? Cutoff : Cutoff.AddDays(-2));
        decorator.LegacySummaryCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ActivityFallback_OrdersByPinThenPinTimeThenEffectiveActivityThenOrdinalId()
    {
        var inner = new InMemoryConversationStore();
        IConversationStore store = new LegacyStore(inner);
        foreach (var conversation in new[]
        {
            Make("b") with { UpdatedAt = Cutoff },
            Make("a") with { UpdatedAt = Cutoff },
            Make("latest") with { ChannelBindings = [new ChannelBinding { LastOutboundAt = Cutoff.AddHours(1) }] },
            Make("pin-old") with { IsPinned = true, PinnedAt = Cutoff.AddDays(-2) },
            Make("pin-new") with { IsPinned = true, PinnedAt = Cutoff.AddDays(-1) }
        })
            await inner.CreateAsync(conversation);
        (await store.GetSummariesAsync((DateTimeOffset?)null)).Select(s => s.ConversationId)
            .ShouldBe(new[] { "pin-new", "pin-old", "latest", "a", "b" });
    }

    [Fact]
    public async Task ActivityFallback_PropagatesCancellationAndPassesTokenToList()
    {
        var decorator = new LegacyStore(new InMemoryConversationStore());
        IConversationStore store = decorator;
        using var source = new CancellationTokenSource();
        await store.GetSummariesAsync(Cutoff, source.Token);
        decorator.ListToken.ShouldBe(source.Token);
        source.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => store.GetSummariesAsync(Cutoff, source.Token));
    }

    private static Conversation Make(string id) => new()
    {
        ConversationId = ConversationId.From(id), AgentId = AgentId.From("owner"), Title = id,
        CreatedAt = Cutoff.AddDays(-3), UpdatedAt = Cutoff.AddTicks(-1)
    };

    // Deliberately implements only the legacy required members. Activity calls must dispatch
    // to the interface default, never to the inner store's optimized Activity overload.
    private sealed class LegacyStore(IConversationStore inner) : IConversationStore
    {
        public int LegacySummaryCalls { get; private set; }
        public CancellationToken ListToken { get; private set; }
        public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
        {
            ListToken = ct;
            return inner.ListAsync(agentId, ct);
        }
        public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
        {
            LegacySummaryCalls++;
            return inner.GetSummariesAsync(ct);
        }
        public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default) => inner.GetAsync(conversationId, ct);
        public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default) => inner.ListForCitizenAsync(citizen, ct);
        public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default) => inner.AddParticipantsAsync(conversationId, participants, ct);
        public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default) => inner.CreateAsync(conversation, ct);
        public Task SaveAsync(Conversation conversation, CancellationToken ct = default) => inner.SaveAsync(conversation, ct);
        public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default) => inner.ArchiveAsync(conversationId, ct);
        public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default) => inner.ResolveByBindingAsync(agentId, channelType, channelAddress, ct);
        public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default) => inner.TouchAsync(conversationId, ct);
        public Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default) => inner.PinAsync(conversationId, pin, ct);
        public Task<bool> AddBindingAsync(ConversationId conversationId, ChannelBinding binding, CancellationToken ct = default) => inner.AddBindingAsync(conversationId, binding, ct);
        public Task<bool> RemoveBindingAsync(ConversationId conversationId, BindingId bindingId, CancellationToken ct = default) => inner.RemoveBindingAsync(conversationId, bindingId, ct);
        public Task<bool> MoveBindingAsync(ConversationId fromConversationId, ConversationId toConversationId, BindingId bindingId, CancellationToken ct = default) => inner.MoveBindingAsync(fromConversationId, toConversationId, bindingId, ct);
        public Task<Conversation?> PatchMetadataAsync(ConversationId conversationId, ConversationMetadataPatch patch, CancellationToken ct = default) => inner.PatchMetadataAsync(conversationId, patch, ct);
        public Task<Conversation?> PatchOverrideAsync(ConversationId conversationId, ConversationOverridePatch patch, CancellationToken ct = default) => inner.PatchOverrideAsync(conversationId, patch, ct);
        public Task<IReadOnlyList<PendingAskUserCheckpoint>> GetPendingAskUserCheckpointsAsync(CancellationToken ct = default) => inner.GetPendingAskUserCheckpointsAsync(ct);
        public Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default) => inner.GetCanvasStateAsync(conversationId, ct);
        public Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default) => inner.SetCanvasStateKeyAsync(conversationId, key, value, ct);
        public Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default) => inner.DeleteCanvasStateKeyAsync(conversationId, key, ct);
        public Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default) => inner.ClearCanvasStateAsync(conversationId, ct);
    }
}
