using System.Collections.Concurrent;
using System.IO.Abstractions.TestingHelpers;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Contracts.Memory;
using BotNexus.Memory.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BotNexus.Memory.Tests;

/// <summary>
/// #2608 — a sub-agent session whose agent directory has already been reaped by the
/// workspace sweeper is a normal outcome, not an error. The indexer must skip it before
/// SQLite is reached, log at Debug, and consume none of the SQLite retry budget. A genuine
/// store failure must still surface at Error.
/// </summary>
public sealed class MemoryIndexerSweptSessionTests
{
    [Fact]
    public async Task OnSessionClosed_WhenAgentStoreLocationMissing_LogsDebugAndNotError()
    {
        var lifecycle = new TestSessionLifecycleEvents();
        var store = new CountingMemoryStore();
        var factory = new LocationAwareStoreFactory(store, storeLocationExists: false);
        var logger = new RecordingLogger();
        var indexer = new MemoryIndexer(new ThrowingMemoryFactory(), factory, lifecycle, logger);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("swept-1", "agent-swept");
            await lifecycle.RaiseAsync(new SessionLifecycleEvent(
                "swept-1", "agent-swept", SessionLifecycleEventType.Closed, session));

            var record = await logger.WaitForAsync(LogLevel.Debug);

            record.Message.ShouldContain("swept-1");
            logger.Records.ShouldNotContain(entry => entry.Level == LogLevel.Error);
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OnSessionClosed_WhenAgentStoreLocationMissing_ConsumesNoStoreAttempts()
    {
        var lifecycle = new TestSessionLifecycleEvents();
        var store = new CountingMemoryStore();
        var factory = new LocationAwareStoreFactory(store, storeLocationExists: false);
        var logger = new RecordingLogger();
        var indexer = new MemoryIndexer(new ThrowingMemoryFactory(), factory, lifecycle, logger);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("swept-2", "agent-swept");
            await lifecycle.RaiseAsync(new SessionLifecycleEvent(
                "swept-2", "agent-swept", SessionLifecycleEventType.Closed, session));

            await logger.WaitForAsync(LogLevel.Debug);

            // The whole point of #2608: SQLite is never reached, so the retry budget
            // (which is spent inside the store) cannot be consumed.
            factory.CreateCount.ShouldBe(0);
            store.TotalAttempts.ShouldBe(0);
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OnSessionClosed_WhenAgentStoreLocationPresent_StillIndexes()
    {
        // Positive control for the attempt counter used by the skip test above.
        var lifecycle = new TestSessionLifecycleEvents();
        var store = new CountingMemoryStore();
        var factory = new LocationAwareStoreFactory(store, storeLocationExists: true);
        var logger = new RecordingLogger();
        var indexer = new MemoryIndexer(new ThrowingMemoryFactory(), factory, lifecycle, logger);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("live-1", "agent-live");
            await lifecycle.RaiseAsync(new SessionLifecycleEvent(
                "live-1", "agent-live", SessionLifecycleEventType.Closed, session));

            await store.WaitForInsertAsync();

            factory.CreateCount.ShouldBeGreaterThan(0);
            store.TotalAttempts.ShouldBeGreaterThan(0);
            logger.Records.ShouldNotContain(entry => entry.Level == LogLevel.Error);
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task OnSessionClosed_WhenStorePresentButFails_StillLogsError()
    {
        // Discrimination, not suppression: a corrupt/unreadable store is still an Error.
        var lifecycle = new TestSessionLifecycleEvents();
        var store = new CountingMemoryStore { FailWith = new SqliteException("database disk image is malformed", 11) };
        var factory = new LocationAwareStoreFactory(store, storeLocationExists: true);
        var logger = new RecordingLogger();
        var indexer = new MemoryIndexer(new ThrowingMemoryFactory(), factory, lifecycle, logger);
        await indexer.StartAsync(CancellationToken.None);

        try
        {
            var session = CreateSession("corrupt-1", "agent-live");
            await lifecycle.RaiseAsync(new SessionLifecycleEvent(
                "corrupt-1", "agent-live", SessionLifecycleEventType.Closed, session));

            var record = await logger.WaitForAsync(LogLevel.Error);

            record.Message.ShouldContain("corrupt-1");
            record.Exception.ShouldBeOfType<SqliteException>();
        }
        finally
        {
            await indexer.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void MemoryStoreFactory_StoreLocationExists_IsFalseWhenAgentDirectoryMissing()
    {
        var fileSystem = new MockFileSystem();
        var root = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "botnexus-2608", "agents");
        var factory = new MemoryStoreFactory(
            agentId => fileSystem.Path.Combine(root, agentId, "data", "memory.sqlite"),
            fileSystem);

        factory.StoreLocationExists("swept-agent").ShouldBeFalse();
    }

    [Fact]
    public void MemoryStoreFactory_StoreLocationExists_IsTrueWhenAgentDirectoryPresentWithoutDataFolder()
    {
        // A brand new agent has a directory but no data/ subfolder yet — that must still
        // be indexed (the store creates data/ on initialize), so it is not a skip.
        var fileSystem = new MockFileSystem();
        var root = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "botnexus-2608", "agents");
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(root, "live-agent"));
        var factory = new MemoryStoreFactory(
            agentId => fileSystem.Path.Combine(root, agentId, "data", "memory.sqlite"),
            fileSystem);

        factory.StoreLocationExists("live-agent").ShouldBeTrue();
    }

    [Fact]
    public async Task SqliteRetryHelper_CantOpen_IsTerminal_AndUsesASingleAttempt()
    {
        var attempts = 0;

        await Should.ThrowAsync<SqliteException>(async () =>
            await SqliteRetryHelper.ExecuteWithRetryAsync<int>(
                _ =>
                {
                    attempts++;
                    throw new SqliteException("unable to open database file", 14);
                },
                CancellationToken.None));

        attempts.ShouldBe(1);
    }

    [Fact]
    public async Task SqliteRetryHelper_Busy_IsTransient_AndUsesTheFullBudget()
    {
        var attempts = 0;

        await Should.ThrowAsync<SqliteException>(async () =>
            await SqliteRetryHelper.ExecuteWithRetryAsync<int>(
                _ =>
                {
                    attempts++;
                    throw new SqliteException("database is locked", 5);
                },
                CancellationToken.None));

        attempts.ShouldBe(3);
    }

    private static GatewaySession CreateSession(string sessionId, string agentId)
    {
        var session = new GatewaySession
        {
            SessionId = SessionId.From(sessionId),
            AgentId = AgentId.From(agentId)
        };

        session.AddEntries(
        [
            new SessionEntry { Role = MessageRole.User, Content = "Hello" },
            new SessionEntry { Role = MessageRole.Assistant, Content = "Hi there" }
        ]);

        return session;
    }

    private sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<MemoryIndexer>
    {
        private readonly ConcurrentQueue<LogRecord> _records = new();
        private readonly ConcurrentDictionary<LogLevel, TaskCompletionSource<LogRecord>> _waiters = new();

        public IReadOnlyList<LogRecord> Records => _records.ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var record = new LogRecord(logLevel, formatter(state, exception), exception);
            _records.Enqueue(record);
            Waiter(logLevel).TrySetResult(record);
        }

        public async Task<LogRecord> WaitForAsync(LogLevel level)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await Waiter(level).Task.WaitAsync(cts.Token);
        }

        private TaskCompletionSource<LogRecord> Waiter(LogLevel level)
            => _waiters.GetOrAdd(level, _ => new TaskCompletionSource<LogRecord>(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    private sealed class ThrowingMemoryFactory : IAgentMemoryFactory
    {
        public IAgentMemory Create(string agentId, string? providerName = null)
            => throw new NotSupportedException();

        public IReadOnlyList<string> GetRegisteredProviders() => [];
    }

    private sealed class TestSessionLifecycleEvents : ISessionLifecycleEvents
    {
        public event Func<SessionLifecycleEvent, CancellationToken, Task>? SessionChanged;

        public Task RaiseAsync(SessionLifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
            => SessionChanged?.Invoke(lifecycleEvent, cancellationToken) ?? Task.CompletedTask;
    }

    private sealed class LocationAwareStoreFactory(CountingMemoryStore store, bool storeLocationExists) : IMemoryStoreFactory
    {
        private int _createCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public IMemoryStore Create(string agentId)
        {
            Interlocked.Increment(ref _createCount);
            return store;
        }

        public bool StoreLocationExists(string agentId) => storeLocationExists;
    }

    private sealed class CountingMemoryStore : IMemoryStore
    {
        private readonly ConcurrentDictionary<string, MemoryEntry> _entries = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<bool> _inserted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _attempts;

        public SqliteException? FailWith { get; init; }

        public int TotalAttempts => Volatile.Read(ref _attempts);

        public Task InitializeAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            return Task.CompletedTask;
        }

        public Task<MemoryEntry> InsertAsync(MemoryEntry entry, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            var id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id;
            var inserted = entry with { Id = id };
            _entries[id] = inserted;
            _inserted.TrySetResult(true);
            return Task.FromResult(inserted);
        }

        public Task<MemoryEntry?> GetByIdAsync(string id, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            _entries.TryGetValue(id, out var entry);
            return Task.FromResult(entry);
        }

        public Task<IReadOnlyList<MemoryEntry>> GetBySessionAsync(string sessionId, int limit = 20, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            if (FailWith is not null)
                return Task.FromException<IReadOnlyList<MemoryEntry>>(FailWith);

            var results = _entries.Values.Where(entry => entry.SessionId == sessionId).Take(limit).ToList();
            return Task.FromResult<IReadOnlyList<MemoryEntry>>(results);
        }

        public Task<IReadOnlyList<MemoryEntry>> SearchAsync(string query, int topK = 10, MemorySearchFilter? filter = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        }

        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            _entries.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            _entries.Clear();
            return Task.CompletedTask;
        }

        public Task<MemoryStoreStats> GetStatsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _attempts);
            return Task.FromResult(new MemoryStoreStats(_entries.Count, 0, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async Task WaitForInsertAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await _inserted.Task.WaitAsync(cts.Token);
        }
    }
}
