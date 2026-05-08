using System.Collections.Concurrent;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Memory.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Memory.Tests;

public sealed class MemoryIndexerTests
{
    [Fact]
    public async Task OnSessionClosed_IndexesConversationExchanges()
    {
        var lifecycle = new TestSessionLifecycleEvents();
        var store = new FakeMemoryStore();
        var factory = new FakeMemoryStoreFactory(store);
        var indexer = new MemoryIndexer(factory, lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("session-1", "agent-a", [
                new SessionEntry { Role = MessageRole.User, Content = "Hello" },
                new SessionEntry { Role = MessageRole.Assistant, Content = "Hi there" },
                new SessionEntry { Role = MessageRole.User, Content = "How are you?" },
                new SessionEntry { Role = MessageRole.Assistant, Content = "Doing great" }
            ]);

            await lifecycle.RaiseAsync(new SessionLifecycleEvent("session-1", "agent-a", SessionLifecycleEventType.Closed, session));
            await WaitForAsync(() => store.GetAll().Count == 2);

            var entries = store.GetAll().OrderBy(entry => entry.TurnIndex).ToList();
            entries.Count().ShouldBe(2);
            entries[0].Content.ShouldContain("User: Hello");
            entries[0].Content.ShouldContain("Assistant: Hi there");
            entries[1].Content.ShouldContain("User: How are you?");
            entries[1].Content.ShouldContain("Assistant: Doing great");
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OnSessionClosed_SkipsToolRoleEntries()
    {
        var lifecycle = new TestSessionLifecycleEvents();
        var store = new FakeMemoryStore();
        var factory = new FakeMemoryStoreFactory(store);
        var indexer = new MemoryIndexer(factory, lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("session-2", "agent-a", [
                new SessionEntry { Role = MessageRole.User, Content = "Run search" },
                new SessionEntry { Role = MessageRole.Tool, Content = "tool output", ToolName = "memory_search" },
                new SessionEntry { Role = MessageRole.Assistant, Content = "Here is what I found" }
            ]);

            await lifecycle.RaiseAsync(new SessionLifecycleEvent("session-2", "agent-a", SessionLifecycleEventType.Closed, session));
            await WaitForAsync(() => store.GetAll().Count == 1);

            var indexed = store.GetAll().Single();
            indexed.Content.ShouldContain("User: Run search");
            indexed.Content.ShouldContain("Assistant: Here is what I found");
            indexed.Content.ShouldNotContain("tool output");
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OnSessionClosed_IsIdempotent_NoDuplicates()
    {
        var lifecycle = new TestSessionLifecycleEvents();
        var store = new FakeMemoryStore();
        var factory = new FakeMemoryStoreFactory(store);
        var indexer = new MemoryIndexer(factory, lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("session-3", "agent-a", [
                new SessionEntry { Role = MessageRole.User, Content = "Remember me" },
                new SessionEntry { Role = MessageRole.Assistant, Content = "I will" }
            ]);

            var closedEvent = new SessionLifecycleEvent("session-3", "agent-a", SessionLifecycleEventType.Closed, session);
            await lifecycle.RaiseAsync(closedEvent);
            await WaitForAsync(() => store.GetAll().Count == 1);
            await lifecycle.RaiseAsync(closedEvent);
            await Task.Delay(100);

            store.GetAll().Count().ShouldBe(1);
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OnSessionExpired_AlsoIndexes()
    {
        var lifecycle = new TestSessionLifecycleEvents();
        var store = new FakeMemoryStore();
        var factory = new FakeMemoryStoreFactory(store);
        var indexer = new MemoryIndexer(factory, lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("session-4", "agent-a", [
                new SessionEntry { Role = MessageRole.User, Content = "Ping" },
                new SessionEntry { Role = MessageRole.Assistant, Content = "Pong" }
            ]);

            await lifecycle.RaiseAsync(new SessionLifecycleEvent("session-4", "agent-a", SessionLifecycleEventType.Expired, session));
            await WaitForAsync(() => store.GetAll().Count == 1);

            store.GetAll().Single().SourceType.ShouldBe("conversation");
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    private static GatewaySession CreateSession(string sessionId, string agentId, IReadOnlyList<SessionEntry> entries)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(agentId)
        };

        session.AddEntries(entries);
        return session;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMs)
                throw new TimeoutException("Timed out waiting for asynchronous indexing to complete.");

            await Task.Delay(20);
        }
    }

    private sealed class TestSessionLifecycleEvents : ISessionLifecycleEvents
    {
        public event Func<SessionLifecycleEvent, CancellationToken, Task>? SessionChanged;

        public Task RaiseAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
            => SessionChanged?.Invoke(lifecycleEvent, cancellationToken) ?? Task.CompletedTask;
    }

    private sealed class FakeMemoryStoreFactory(FakeMemoryStore store) : IMemoryStoreFactory
    {
        private readonly FakeMemoryStore _store = store;
        public IMemoryStore Create(string agentId) => _store;
    }

    private sealed class FakeMemoryStore : IMemoryStore
    {
        private readonly ConcurrentDictionary<string, MemoryEntry> _entries = new(StringComparer.Ordinal);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default)
        {
            var inserted = entry with
            {
                Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
                CreatedAt = entry.CreatedAt == default ? DateTimeOffset.UtcNow : entry.CreatedAt
            };

            _entries[inserted.Id] = inserted;
            return Task.FromResult(inserted);
        }

        public Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default)
        {
            _entries.TryGetValue(id, out var entry);
            return Task.FromResult(entry);
        }

        public Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
        {
            var results = _entries.Values
                .Where(entry => entry.SessionId == sessionId)
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(limit)
                .ToList();

            return Task.FromResult<IReadOnlyList<MemoryEntry>>(results);
        }

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            _entries.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }

        public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default)
            => Task.FromResult(new MemoryStoreStats(_entries.Count, 0, _entries.Values.MaxBy(entry => entry.CreatedAt)?.CreatedAt));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public IReadOnlyList<MemoryEntry> GetAll() => _entries.Values.ToList();
    }
}

