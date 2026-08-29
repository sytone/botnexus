using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Services;

/// <summary>
/// Verifies startup reconciliation (issue #2047): durable ask_user checkpoints are rehydrated into
/// the response registry so an inbound answer after a restart is recognised rather than
/// mis-dispatched as a fresh turn.
/// </summary>
public sealed class AskUserCheckpointReconciliationServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task StartAsync_RehydratesPendingCheckpointsIntoRegistry()
    {
        var store = new InMemoryConversationStore();
        var registry = new AskUserResponseRegistry();

        var withPrompt = ConversationId.From("conv-pending");
        await store.CreateAsync(new Conversation
        {
            ConversationId = withPrompt,
            AgentId = AgentId.From("agent-a"),
            Title = "pending",
            PendingAskUserJson = SerializeRequest(withPrompt, "req-1")
        });

        var withoutPrompt = ConversationId.From("conv-clean");
        await store.CreateAsync(new Conversation
        {
            ConversationId = withoutPrompt,
            AgentId = AgentId.From("agent-a"),
            Title = "clean"
        });

        var service = new AskUserCheckpointReconciliationService(
            store, registry, NullLogger<AskUserCheckpointReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);

        registry.TryGetPendingRequestId(withPrompt, out var requestId).ShouldBeTrue();
        requestId.ShouldBe("req-1");
        registry.TryGetPendingRequestId(withoutPrompt, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task StartAsync_SkipsUnparseableCheckpointWithoutThrowing()
    {
        var store = new InMemoryConversationStore();
        var registry = new AskUserResponseRegistry();
        var conversationId = ConversationId.From("conv-corrupt");
        await store.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("agent-a"),
            Title = "corrupt",
            PendingAskUserJson = "not json ]["
        });

        var service = new AskUserCheckpointReconciliationService(
            store, registry, NullLogger<AskUserCheckpointReconciliationService>.Instance);

        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
        registry.TryGetPendingRequestId(conversationId, out _).ShouldBeFalse();
    }

    /// <summary>
    /// #3660 acceptance criterion 1: reconciliation must reach the store through the narrow
    /// pending-checkpoint query, never <c>ListAsync</c>. This is the behavioural half of the fence
    /// — a decorator records which store method the service actually calls, so reintroducing the
    /// full scan fails here as well as in the architecture fence.
    /// </summary>
    [Fact]
    public async Task StartAsync_QueriesOnlyPendingCheckpoints_AndNeverCallsListAsync()
    {
        var inner = new InMemoryConversationStore();
        var conversationId = ConversationId.From("conv-pending");
        await inner.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = AgentId.From("agent-a"),
            Title = "pending",
            PendingAskUserJson = SerializeRequest(conversationId, "req-1")
        });
        for (var i = 0; i < 20; i++)
        {
            await inner.CreateAsync(new Conversation
            {
                ConversationId = ConversationId.From($"conv-noise-{i}"),
                AgentId = AgentId.From("agent-a"),
                Title = $"noise-{i}"
            });
        }

        var store = new CallRecordingConversationStore(inner);
        var registry = new AskUserResponseRegistry();
        var service = new AskUserCheckpointReconciliationService(
            store, registry, NullLogger<AskUserCheckpointReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);

        store.ListAsyncCallCount.ShouldBe(0,
            "#3660: startup reconciliation must not materialise the conversation population");
        store.PendingCheckpointCallCount.ShouldBe(1);
        // Non-vacuity: the rehydration really happened, so a zero ListAsync count is a narrow
        // query succeeding rather than the service doing nothing at all.
        registry.TryGetPendingRequestId(conversationId, out var requestId).ShouldBeTrue();
        requestId.ShouldBe("req-1");
    }

    /// <summary>
    /// #3660 acceptance criterion 6: multiple pending checkpoints are all rehydrated.
    /// </summary>
    [Fact]
    public async Task StartAsync_RehydratesEveryPendingCheckpoint()
    {
        var store = new InMemoryConversationStore();
        var registry = new AskUserResponseRegistry();
        var ids = new List<ConversationId>();
        for (var i = 0; i < 3; i++)
        {
            var id = ConversationId.From($"conv-pending-{i}");
            await store.CreateAsync(new Conversation
            {
                ConversationId = id,
                AgentId = AgentId.From("agent-a"),
                Title = $"pending-{i}",
                PendingAskUserJson = SerializeRequest(id, $"req-{i}")
            });
            ids.Add(id);
        }

        var service = new AskUserCheckpointReconciliationService(
            store, registry, NullLogger<AskUserCheckpointReconciliationService>.Instance);

        await service.StartAsync(CancellationToken.None);

        for (var i = 0; i < ids.Count; i++)
        {
            registry.TryGetPendingRequestId(ids[i], out var requestId).ShouldBeTrue();
            requestId.ShouldBe($"req-{i}");
        }
    }

    /// <summary>
    /// #3660 acceptance criterion 7: a store failure is best-effort — it is swallowed so the
    /// gateway still reaches <c>app.Run()</c> and binds its port. A throwing reconciliation would
    /// abort the host and turn a diagnostics gap into an outage.
    /// </summary>
    [Fact]
    public async Task StartAsync_SwallowsStoreFailure_SoStartupIsNeverBlocked()
    {
        var store = new ThrowingPendingCheckpointStore();
        var registry = new AskUserResponseRegistry();
        var service = new AskUserCheckpointReconciliationService(
            store, registry, NullLogger<AskUserCheckpointReconciliationService>.Instance);

        await Should.NotThrowAsync(() => service.StartAsync(CancellationToken.None));
        store.CallCount.ShouldBe(1, "non-vacuity: the failing path was actually exercised");
    }

    /// <summary>
    /// #3660 acceptance criterion 6: cancellation propagates rather than being absorbed by the
    /// best-effort catch, so a shutdown during startup is not misreported as a completed
    /// reconciliation.
    /// </summary>
    [Fact]
    public async Task StartAsync_PropagatesCancellation()
    {
        var store = new InMemoryConversationStore();
        var registry = new AskUserResponseRegistry();
        var service = new AskUserCheckpointReconciliationService(
            store, registry, NullLogger<AskUserCheckpointReconciliationService>.Instance);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => service.StartAsync(cts.Token));
    }

    private static string SerializeRequest(ConversationId conversationId, string requestId)
        => JsonSerializer.Serialize(new AskUserRequest
        {
            RequestId = requestId,
            ConversationId = conversationId,
            SessionId = SessionId.From("session-old"),
            AgentId = AgentId.From("agent-a"),
            Prompt = "restored prompt"
        }, JsonOptions);
}

/// <summary>
/// #3660: records which <see cref="IConversationStore"/> read the reconciliation service actually
/// performs, so "does not call ListAsync" is an observed fact rather than an inspection of source.
/// </summary>
internal sealed class CallRecordingConversationStore(IConversationStore inner) : IConversationStore
{
    /// <summary>Number of full-population scans performed. Must stay zero for #3660.</summary>
    public int ListAsyncCallCount { get; private set; }

    /// <summary>Number of narrow pending-checkpoint queries performed.</summary>
    public int PendingCheckpointCallCount { get; private set; }

    public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
    {
        ListAsyncCallCount++;
        return inner.ListAsync(agentId, ct);
    }

    public Task<IReadOnlyList<PendingAskUserCheckpoint>> GetPendingAskUserCheckpointsAsync(CancellationToken ct = default)
    {
        PendingCheckpointCallCount++;
        return inner.GetPendingAskUserCheckpointsAsync(ct);
    }

    public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
        => inner.GetAsync(conversationId, ct);
    public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
        => inner.ListForCitizenAsync(citizen, ct);
    public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default)
        => inner.AddParticipantsAsync(conversationId, participants, ct);
    public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
        => inner.CreateAsync(conversation, ct);
    public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
        => inner.SaveAsync(conversation, ct);
    public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
        => inner.ArchiveAsync(conversationId, ct);
    public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
        => inner.ResolveByBindingAsync(agentId, channelType, channelAddress, ct);
    public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default)
        => inner.TouchAsync(conversationId, ct);
    public Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default)
        => inner.PinAsync(conversationId, pin, ct);
    public Task<bool> AddBindingAsync(ConversationId conversationId, ChannelBinding binding, CancellationToken ct = default)
        => inner.AddBindingAsync(conversationId, binding, ct);
    public Task<bool> RemoveBindingAsync(ConversationId conversationId, BindingId bindingId, CancellationToken ct = default)
        => inner.RemoveBindingAsync(conversationId, bindingId, ct);
    public Task<bool> MoveBindingAsync(ConversationId fromConversationId, ConversationId toConversationId, BindingId bindingId, CancellationToken ct = default)
        => inner.MoveBindingAsync(fromConversationId, toConversationId, bindingId, ct);
    public Task<Conversation?> PatchMetadataAsync(ConversationId conversationId, ConversationMetadataPatch patch, CancellationToken ct = default)
        => inner.PatchMetadataAsync(conversationId, patch, ct);
    public Task<Conversation?> PatchOverrideAsync(ConversationId conversationId, ConversationOverridePatch patch, CancellationToken ct = default)
        => inner.PatchOverrideAsync(conversationId, patch, ct);
    public Task<IReadOnlyList<ConversationSummary>> GetSummariesAsync(CancellationToken ct = default)
        => inner.GetSummariesAsync(ct);
    public Task<Dictionary<string, JsonElement>?> GetCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
        => inner.GetCanvasStateAsync(conversationId, ct);
    public Task<bool> SetCanvasStateKeyAsync(ConversationId conversationId, string key, JsonElement value, CancellationToken ct = default)
        => inner.SetCanvasStateKeyAsync(conversationId, key, value, ct);
    public Task DeleteCanvasStateKeyAsync(ConversationId conversationId, string key, CancellationToken ct = default)
        => inner.DeleteCanvasStateKeyAsync(conversationId, key, ct);
    public Task ClearCanvasStateAsync(ConversationId conversationId, CancellationToken ct = default)
        => inner.ClearCanvasStateAsync(conversationId, ct);
}

/// <summary>
/// #3660 (criterion 7): a store whose pending-checkpoint query always fails, proving reconciliation
/// stays best-effort and never prevents the gateway from listening.
/// </summary>
internal sealed class ThrowingPendingCheckpointStore : IConversationStore
{
    private readonly InMemoryConversationStore _inner = new();

    /// <summary>How many times the failing query was invoked.</summary>
    public int CallCount { get; private set; }

    public Task<IReadOnlyList<PendingAskUserCheckpoint>> GetPendingAskUserCheckpointsAsync(CancellationToken ct = default)
    {
        CallCount++;
        throw new InvalidOperationException("simulated conversation store failure");
    }

    public Task<Conversation?> GetAsync(ConversationId conversationId, CancellationToken ct = default)
        => _inner.GetAsync(conversationId, ct);
    public Task<IReadOnlyList<Conversation>> ListAsync(AgentId? agentId = null, CancellationToken ct = default)
        => _inner.ListAsync(agentId, ct);
    public Task<IReadOnlyList<Conversation>> ListForCitizenAsync(CitizenId citizen, CancellationToken ct = default)
        => _inner.ListForCitizenAsync(citizen, ct);
    public Task AddParticipantsAsync(ConversationId conversationId, IEnumerable<SessionParticipant> participants, CancellationToken ct = default)
        => _inner.AddParticipantsAsync(conversationId, participants, ct);
    public Task<Conversation> CreateAsync(Conversation conversation, CancellationToken ct = default)
        => _inner.CreateAsync(conversation, ct);
    public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
        => _inner.SaveAsync(conversation, ct);
    public Task ArchiveAsync(ConversationId conversationId, CancellationToken ct = default)
        => _inner.ArchiveAsync(conversationId, ct);
    public Task<Conversation?> ResolveByBindingAsync(AgentId agentId, ChannelKey channelType, ChannelAddress channelAddress, CancellationToken ct = default)
        => _inner.ResolveByBindingAsync(agentId, channelType, channelAddress, ct);
    public Task TouchAsync(ConversationId conversationId, CancellationToken ct = default)
        => _inner.TouchAsync(conversationId, ct);
    public Task PinAsync(ConversationId conversationId, bool pin, CancellationToken ct = default)
        => _inner.PinAsync(conversationId, pin, ct);
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