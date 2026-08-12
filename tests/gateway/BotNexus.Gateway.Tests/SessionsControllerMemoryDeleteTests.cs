using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Security;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using BotNexus.Memory;
using BotNexus.Memory.Embeddings;
using BotNexus.Memory.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Issue #2956 clause 2: deleting a session through the API must remove that session's memory
/// rows, not just the session row. Before this, deletion was cosmetic - the content stayed
/// searchable in the agent's memory store forever.
/// </summary>
public sealed class SessionsControllerMemoryDeleteTests
{
    [Fact]
    public async Task Delete_RemovesThatSessionsMemoryRows()
    {
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        var memory = new RecordingMemoryStore();
        var controller = new SessionsController(store, memoryStoreFactory: new SingleStoreFactory(memory));

        var result = await controller.Delete("s1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        memory.DeletedSessionIds.ShouldBe(["s1"], "the deleted session's memory rows must be pruned in the same operation");
    }

    [Fact]
    public async Task Delete_WhenAuthorizationFails_DoesNotDeleteMemoryRows()
    {
        // Sad path: a refused delete must not prune memory either, or a 403 would still
        // destroy the caller's recall.
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));
        session.CallerId = "owner";
        await store.SaveAsync(session);

        var memory = new RecordingMemoryStore();
        var controller = new SessionsController(store, memoryStoreFactory: new SingleStoreFactory(memory))
        {
            ControllerContext = CreateControllerContext("someone-else")
        };

        var result = await controller.Delete("s1", CancellationToken.None);

        result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(403);
        memory.DeletedSessionIds.ShouldBeEmpty("an unauthorized delete must not prune memory rows");
    }

    [Fact]
    public async Task Delete_WhenMemoryPruneThrows_StillReturnsNoContent()
    {
        // The session delete has already committed by then; surfacing a 500 would make the
        // endpoint non-idempotent on retry for a failure the caller cannot act on.
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        var controller = new SessionsController(store, memoryStoreFactory: new SingleStoreFactory(new ThrowingMemoryStore()));

        var result = await controller.Delete("s1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        (await store.GetAsync(SessionId.From("s1"), CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Delete_WithNoMemoryStoreConfigured_StillReturnsNoContent()
    {
        // Memory is optional; the controller must resolve without it.
        var store = new InMemorySessionStore();
        await store.GetOrCreateAsync(SessionId.From("s1"), AgentId.From("agent-a"));

        var controller = new SessionsController(store);

        var result = await controller.Delete("s1", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
    }

    private const string CallerIdentityItemKey = "BotNexus.Gateway.CallerIdentity";

    private static ControllerContext CreateControllerContext(string callerId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CallerIdentityItemKey] = new GatewayCallerIdentity
        {
            CallerId = callerId
        };

        return new ControllerContext { HttpContext = httpContext };
    }

    private sealed class SingleStoreFactory(IMemoryStore store) : IMemoryStoreFactory
    {
        public IMemoryStore Create(string agentId) => store;
    }

    private class RecordingMemoryStore : IMemoryStore
    {
        public List<string> DeletedSessionIds { get; } = [];

        public virtual Task<int> DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
        {
            DeletedSessionIds.Add(sessionId);
            return Task.FromResult(1);
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default) => Task.FromResult(entry);
        public Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default) => Task.FromResult<MemoryEntry?>(null);
        public Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        // Required (not default-implemented) on IMemoryStore by design (#2781): Moq returns null for a
        // default interface method instead of running its body, so every mocked store would silently
        // yield a null task. These tests exercise session-scoped deletion and never search, so an empty
        // result is the honest stub -- it mirrors SearchAsync above.
        public Task<IReadOnlyList<ScoredMemoryEntry>> SearchScoredAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScoredMemoryEntry>>([]);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new MemoryStoreStats(0, 0, null));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingMemoryStore : RecordingMemoryStore
    {
        public override Task<int> DeleteBySessionAsync(string sessionId, CancellationToken ct = default)
            => throw new InvalidOperationException("memory store unavailable");
    }
}
