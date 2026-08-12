using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Memory.Tests.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Memory.Tests;

/// <summary>
/// Pins the startup reconciliation added for issue #2956: memory rows whose session_id names
/// a session that no longer exists are pruned, and the scan fails closed rather than deleting
/// on a partial or failed read of the session corpus.
/// </summary>
public sealed class MemorySessionReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_PrunesRowsWhoseSessionNoLongerExists()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", "agent-a", "orphan", sessionId: "s-gone"));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m2", "agent-a", "live", sessionId: "s-live"));

        var reconciler = CreateReconciler(context.Store, new StubSessionStore("s-live"));

        var result = await reconciler.ReconcileAsync(CancellationToken.None);

        result.PrunedRows.ShouldBe(1);
        result.FailedClosed.ShouldBeFalse();
        (await context.Store.GetBySessionAsync("s-gone")).ShouldBeEmpty("the orphaned session's rows must be pruned");
        (await context.Store.GetBySessionAsync("s-live")).Count.ShouldBe(1, "a live session's rows must survive");
    }

    [Fact]
    public async Task ReconcileAsync_WhenSessionCorpusScanThrows_DeletesNothingAndFailsClosed()
    {
        // Clause 4 of #2956. A failed enumeration is indistinguishable from "no sessions
        // exist", and treating it as the latter would delete the entire memory corpus.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", "agent-a", "orphan looking", sessionId: "s-gone"));

        var reconciler = CreateReconciler(context.Store, new ThrowingSessionStore());

        var result = await reconciler.ReconcileAsync(CancellationToken.None);

        result.FailedClosed.ShouldBeTrue("a session corpus scan error must fail closed");
        result.PrunedRows.ShouldBe(0, "no rows may be deleted when the session corpus could not be read");
        (await context.Store.GetByIdAsync("m1")).ShouldNotBeNull();
    }

    [Fact]
    public async Task ReconcileAsync_NeverPrunesRowsWithNullSessionId()
    {
        // Clause 5 of #2956, and the target of the clause-6 mutation check: removing the
        // orphan filter so the reconciler deletes unconditionally must redden THIS test.
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", "agent-a", "manually saved", sessionId: null));
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m2", "agent-a", "orphan", sessionId: "s-gone"));

        var reconciler = CreateReconciler(context.Store, new StubSessionStore());

        var result = await reconciler.ReconcileAsync(CancellationToken.None);

        result.PrunedRows.ShouldBe(1, "only the session-scoped orphan may be pruned");
        (await context.Store.GetByIdAsync("m1")).ShouldNotBeNull(
            "a memory row with a NULL session_id is not session-scoped and must never be pruned by reconciliation");
    }

    [Fact]
    public async Task ReconcileAsync_WithNoOrphans_DeletesNothing()
    {
        await using var context = await MemoryStoreTestContext.CreateAsync();
        await context.Store.InsertAsync(MemoryStoreTestContext.CreateEntry("m1", "agent-a", "live", sessionId: "s-live"));

        var reconciler = CreateReconciler(context.Store, new StubSessionStore("s-live"));

        var result = await reconciler.ReconcileAsync(CancellationToken.None);

        result.PrunedRows.ShouldBe(0);
        result.FailedClosed.ShouldBeFalse();
        (await context.Store.GetByIdAsync("m1")).ShouldNotBeNull();
    }

    private static MemorySessionReconciler CreateReconciler(IMemoryStore store, ISessionStore sessions)
        => new(
            new SingleStoreFactory(store),
            sessions,
            new StubAgentRegistry("agent-a"),
            NullLogger<MemorySessionReconciler>.Instance);

    private sealed class SingleStoreFactory(IMemoryStore store) : IMemoryStoreFactory
    {
        public IMemoryStore Create(string agentId) => store;
    }

    private sealed class StubAgentRegistry(params string[] agentIds) : IAgentRegistry
    {
        private readonly IReadOnlyList<AgentDescriptor> _descriptors =
            [.. agentIds.Select(id => new AgentDescriptor
            {
                AgentId = AgentId.From(id),
                DisplayName = id,
                ModelId = "test-model",
                ApiProvider = "test-provider"
            })];

        public void Register(AgentDescriptor descriptor) => throw new NotSupportedException();
        public void Unregister(AgentId agentId) => throw new NotSupportedException();
        public AgentDescriptor? Get(AgentId agentId) => _descriptors.FirstOrDefault(d => d.AgentId == agentId);
        public IReadOnlyList<AgentDescriptor> GetAll() => _descriptors;
        public bool Contains(AgentId agentId) => _descriptors.Any(d => d.AgentId == agentId);
    }

    private class StubSessionStore(params string[] sessionIds) : ISessionStore
    {
        private readonly IReadOnlyList<GatewaySession> _sessions =
            [.. sessionIds.Select(id => new GatewaySession { SessionId = SessionId.From(id), AgentId = AgentId.From("agent-a") })];

        public virtual Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_sessions);

        public Task<GatewaySession?> GetAsync(SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_sessions.FirstOrDefault(s => s.SessionId == sessionId));

        public Task<GatewaySession> GetOrCreateAsync(SessionId sessionId, AgentId agentId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task SaveAsync(GatewaySession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteAsync(SessionId sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ArchiveAsync(SessionId sessionId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<GatewaySession>> ListByChannelAsync(AgentId agentId, ChannelKey channelType, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
        public Task<IReadOnlyList<GatewaySession>> ListByConversationAsync(ConversationId conversationId, AgentId? agentId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
        public Task<IReadOnlyList<GatewaySession>> GetExistenceAsync(AgentId agentId, ExistenceQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GatewaySession>>([]);
    }

    private sealed class ThrowingSessionStore : StubSessionStore
    {
        public override Task<IReadOnlyList<GatewaySession>> ListAsync(AgentId? agentId = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("session corpus unavailable");
    }
}
