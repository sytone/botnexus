using System.Collections.Concurrent;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Memory.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Memory.Tests;

public sealed class MemoryIndexerAdditionalTests
{
    [Fact]
    public async Task SessionChanged_WithNullSession_IsIgnored()
    {
        var lifecycle = new TestLifecycleEvents();
        var store = new RecordingMemoryStore();
        var indexer = new MemoryIndexer(new TestFactory(store), lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            await lifecycle.RaiseAsync(new SessionLifecycleEvent("s-null", "agent-a", SessionLifecycleEventType.Closed, null));
            await Task.Delay(100);
            store.All.Should().BeEmpty();
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task SessionChanged_ForCreatedEvent_IsIgnored()
    {
        var lifecycle = new TestLifecycleEvents();
        var store = new RecordingMemoryStore();
        var indexer = new MemoryIndexer(new TestFactory(store), lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("s-created", "agent-a",
            [
                new SessionEntry { Role = "user", Content = "hello" },
                new SessionEntry { Role = "assistant", Content = "world" }
            ]);

            await lifecycle.RaiseAsync(new SessionLifecycleEvent("s-created", "agent-a", SessionLifecycleEventType.Created, session));
            await Task.Delay(100);
            store.All.Should().BeEmpty();
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ExpiredSession_WithIncompletePairs_IndexesOnlyValidPairs()
    {
        var lifecycle = new TestLifecycleEvents();
        var store = new RecordingMemoryStore();
        var indexer = new MemoryIndexer(new TestFactory(store), lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("s-expired", "agent-a",
            [
                new SessionEntry { Role = "user", Content = "one" },
                new SessionEntry { Role = "assistant", Content = "first" },
                new SessionEntry { Role = "user", Content = "two" },
                new SessionEntry { Role = "tool", Content = "ignored" },
                new SessionEntry { Role = "assistant", Content = "second" },
                new SessionEntry { Role = "assistant", Content = "orphan" }
            ]);

            await lifecycle.RaiseAsync(new SessionLifecycleEvent("s-expired", "agent-a", SessionLifecycleEventType.Expired, session));
            await WaitForAsync(() => store.All.Count == 2);

            store.All.Should().OnlyContain(entry => entry.SourceType == "conversation");
            store.All.Select(entry => entry.Content).Should().Contain(content => content.Contains("User: one", StringComparison.Ordinal));
            store.All.Select(entry => entry.Content).Should().Contain(content => content.Contains("User: two", StringComparison.Ordinal));
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConcurrentClosedSessions_AreIndexed()
    {
        var lifecycle = new TestLifecycleEvents();
        var store = new RecordingMemoryStore();
        var indexer = new MemoryIndexer(new TestFactory(store), lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var tasks = Enumerable.Range(0, 8)
                .Select(i =>
                {
                    var session = CreateSession($"s-{i}", "agent-a",
                    [
                        new SessionEntry { Role = "user", Content = $"u{i}" },
                        new SessionEntry { Role = "assistant", Content = $"a{i}" }
                    ]);
                    return lifecycle.RaiseAsync(new SessionLifecycleEvent($"s-{i}", "agent-a", SessionLifecycleEventType.Closed, session));
                });

            await Task.WhenAll(tasks);
            await WaitForAsync(() => store.All.Count == 8);
            store.All.Should().HaveCount(8);
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Indexer_RecoversAfterInsertFailure()
    {
        var lifecycle = new TestLifecycleEvents();
        var store = new RecordingMemoryStore { ThrowOnFirstInsert = true };
        var indexer = new MemoryIndexer(new TestFactory(store), lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var first = CreateSession("s-fail", "agent-a",
            [
                new SessionEntry { Role = "user", Content = "u1" },
                new SessionEntry { Role = "assistant", Content = "a1" }
            ]);
            var second = CreateSession("s-pass", "agent-a",
            [
                new SessionEntry { Role = "user", Content = "u2" },
                new SessionEntry { Role = "assistant", Content = "a2" }
            ]);

            await lifecycle.RaiseAsync(new SessionLifecycleEvent("s-fail", "agent-a", SessionLifecycleEventType.Closed, first));
            await store.WaitForFirstInsertAttemptAsync();
            await lifecycle.RaiseAsync(new SessionLifecycleEvent("s-pass", "agent-a", SessionLifecycleEventType.Closed, second));
            await WaitForAsync(() => store.All.Any(entry => entry.SessionId == "s-pass"));

            store.All.Should().ContainSingle(entry => entry.SessionId == "s-pass");
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromEvents()
    {
        var lifecycle = new TestLifecycleEvents();
        var store = new RecordingMemoryStore();
        var indexer = new MemoryIndexer(new TestFactory(store), lifecycle, NullLogger<MemoryIndexer>.Instance);
        await indexer.StartAsync(CancellationToken.None);
        await indexer.StopAsync(CancellationToken.None);

        var session = CreateSession("s-after-stop", "agent-a",
        [
            new SessionEntry { Role = "user", Content = "u" },
            new SessionEntry { Role = "assistant", Content = "a" }
        ]);
        await lifecycle.RaiseAsync(new SessionLifecycleEvent("s-after-stop", "agent-a", SessionLifecycleEventType.Closed, session));
        await Task.Delay(100);

        store.All.Should().BeEmpty();
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var started = DateTime.UtcNow;
        while (!condition())
        {
            if ((DateTime.UtcNow - started).TotalMilliseconds > timeoutMs)
                throw new TimeoutException("Timed out waiting for indexing.");

            await Task.Delay(20);
        }
    }

    private static GatewaySession CreateSession(string sessionId, string agentId, IReadOnlyList<SessionEntry> entries)
    {
        var session = new GatewaySession { SessionId = sessionId, AgentId = agentId };
        session.AddEntries(entries);
        return session;
    }

    private sealed class TestLifecycleEvents : ISessionLifecycleEvents
    {
        public event Func<SessionLifecycleEvent, CancellationToken, Task>? SessionChanged;

        public Task RaiseAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
            => SessionChanged?.Invoke(lifecycleEvent, cancellationToken) ?? Task.CompletedTask;
    }

    private sealed class TestFactory(RecordingMemoryStore store) : IMemoryStoreFactory
    {
        private readonly RecordingMemoryStore _store = store;
        public IMemoryStore Create(string agentId) => _store;
    }

    private sealed class RecordingMemoryStore : IMemoryStore
    {
        private readonly ConcurrentDictionary<string, MemoryEntry> _entries = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _firstInsertAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _insertAttempts;

        public bool ThrowOnFirstInsert { get; set; }
        public IReadOnlyCollection<MemoryEntry> All => _entries.Values.ToList();

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default)
        {
            var attempts = Interlocked.Increment(ref _insertAttempts);
            if (attempts == 1)
                _firstInsertAttempt.TrySetResult(true);
            if (ThrowOnFirstInsert && attempts == 1)
                throw new InvalidOperationException("Simulated failure");

            var id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
            var inserted = entry with { Id = id };
            _entries[id] = inserted;
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
            => Task.FromResult(new MemoryStoreStats(_entries.Count, 0, _entries.Values.OrderByDescending(entry => entry.CreatedAt).FirstOrDefault()?.CreatedAt));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForFirstInsertAttemptAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _firstInsertAttempt.Task.WaitAsync(cts.Token);
        }
    }
}
