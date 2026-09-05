using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>Regression coverage for append-oriented aggregate saves (#3907).</summary>
public sealed class SqliteSessionStoreAppendSaveTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"botnexus-append-save-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly InMemoryConversationStore _conversations = new();

    public SqliteSessionStoreAppendSaveTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _dbPath, Pooling = false }.ToString();
    }

    private SqliteSessionStore CreateStore()
        => new(_connectionString, NullLogger<SqliteSessionStore>.Instance, _conversations);

    private async Task<GatewaySession> CreateSavedSessionAsync(SqliteSessionStore store, string id, int entryCount = 1)
    {
        var agentId = AgentId.From("agent-a");
        var conversationId = ConversationId.Create();
        await _conversations.CreateAsync(new Conversation { ConversationId = conversationId, AgentId = agentId });
        var session = await store.GetOrCreateAsync(SessionId.From(id), agentId);
        session.ConversationId = conversationId;
        session.AddEntries(Enumerable.Range(0, entryCount)
            .Select(i => new SessionEntry { Role = MessageRole.User, Content = $"entry-{i}" }));
        await store.SaveAsync(session);
        return session;
    }

    [Fact]
    public async Task SaveAsync_AfterOneAppend_PreservesPriorRowIdAndInsertsOneRow()
    {
        var seedStore = CreateStore();
        var seeded = await CreateSavedSessionAsync(seedStore, "row-id");
        var firstId = (await ReadHistoryRowsAsync(seeded.SessionId)).Single().Id;

        // Cold hydration must initialize the cursor at the persisted count rather than treating
        // the materialized transcript as a new delta.
        var store = CreateStore();
        var session = await store.GetAsync(seeded.SessionId);
        session.ShouldNotBeNull();
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "second" });
        await store.SaveAsync(session);

        var rows = await ReadHistoryRowsAsync(session.SessionId);
        rows.Count.ShouldBe(2);
        rows[0].Id.ShouldBe(firstId, "an ordinary append must not delete and reinsert the prior row");
        rows[1].Id.ShouldBeGreaterThan(firstId);
        store.LastHistoryRowsMutated.ShouldBe(1);
        store.LastHistoryWriteReconciled.ShouldBeFalse();
    }

    [Fact]
    public async Task SaveAsync_WithLargePersistedHistory_WritesOnlyDelta()
    {
        var store = CreateStore();
        var session = await CreateSavedSessionAsync(store, "large-delta", entryCount: 2_000);

        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "delta" });
        await store.SaveAsync(session);

        store.LastHistoryRowsMutated.ShouldBe(1, "work must be proportional to the delta, not total history");
        store.LastHistoryWriteReconciled.ShouldBeFalse();
        (await ReadHistoryRowsAsync(session.SessionId)).Count.ShouldBe(2_001);
    }

    [Fact]
    public async Task SaveAsync_RepeatedCapturedDelta_IsIdempotentByPersistenceKey()
    {
        var store = CreateStore();
        var session = await CreateSavedSessionAsync(store, "idempotent");
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "once" });
        var snapshot = session.CaptureHistoryForPersistence();
        snapshot.Entries.ShouldHaveSingleItem();

        await store.SaveAsync(session);
        var firstRows = await ReadHistoryRowsAsync(session.SessionId);
        var persisted = snapshot.Entries.Single() with { PersistenceId = null };
        session.AddEntry(persisted);
        await store.SaveAsync(session);

        var rows = await ReadHistoryRowsAsync(session.SessionId);
        rows.Count.ShouldBe(firstRows.Count, "replaying a committed delta with the same persistence key must not duplicate it");
        rows.Count(row => row.Content == "once").ShouldBe(1);
    }

    [Fact]
    public async Task FencedSave_AppendsDeltaAndRoundTripsMetadataAndStatus()
    {
        var store = CreateStore();
        var session = await CreateSavedSessionAsync(store, "fenced");
        var fence = SessionWriteFence.Capture(session);
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "fenced-delta" });
        session.Metadata["key"] = "value";
        session.Status = SessionStatus.Suspended;

        (await store.SaveAsync(session, fence)).ShouldBe(SessionSaveOutcome.Persisted);

        store.LastHistoryRowsMutated.ShouldBe(1);
        store.LastHistoryWriteReconciled.ShouldBeFalse();
        var reloaded = await CreateStore().GetAsync(session.SessionId);
        reloaded.ShouldNotBeNull();
        reloaded.Status.ShouldBe(SessionStatus.Suspended);
        reloaded.Metadata["key"]?.ToString().ShouldBe("value");
        reloaded.GetHistorySnapshot().Select(e => e.Content).ShouldBe(["entry-0", "fenced-delta"]);
    }

    [Fact]
    public async Task SaveAsync_AfterCompactionStyleReplacement_PreservesUnchangedRowIds()
    {
        var seedStore = CreateStore();
        var seeded = await CreateSavedSessionAsync(seedStore, "replacement", entryCount: 3);
        var before = await ReadHistoryRowsAsync(seeded.SessionId);

        var store = CreateStore();
        var session = await store.GetAsync(seeded.SessionId);
        session.ShouldNotBeNull();
        var compacted = session.GetHistorySnapshot()
            .Select(entry => entry with { IsHistory = true })
            .Append(new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true })
            .ToArray();
        session.ReplaceHistory(compacted);

        await store.SaveAsync(session);

        store.LastHistoryWriteReconciled.ShouldBeTrue();
        store.LastHistoryRowsMutated.ShouldBe(4);
        var rows = await ReadHistoryRowsAsync(session.SessionId);
        rows.Take(3).Select(row => row.Id).ShouldBe(before.Select(row => row.Id));
        rows.Take(3).All(row => row.IsHistory).ShouldBeTrue();
        rows[3].Content.ShouldBe("summary");
        rows[3].Id.ShouldBeGreaterThan(before[^1].Id);
    }

    [Fact]
    public async Task SaveAsync_AfterCrashSentinelRemoval_DeletesOnlySentinelId()
    {
        var seedStore = CreateStore();
        var seeded = await CreateSavedSessionAsync(seedStore, "sentinel");
        seeded.AddEntry(new SessionEntry { Role = MessageRole.System, Content = "sentinel", IsCrashSentinel = true });
        seeded.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "after" });
        await seedStore.SaveAsync(seeded);
        var before = await ReadHistoryRowsAsync(seeded.SessionId);
        var sentinelId = before.Single(row => row.Content == "sentinel").Id;

        var store = CreateStore();
        var session = await store.GetAsync(seeded.SessionId);
        session.ShouldNotBeNull();
        session.RemoveCrashSentinels();
        await store.SaveAsync(session);

        store.LastHistoryWriteReconciled.ShouldBeTrue();
        store.LastHistoryRowsMutated.ShouldBe(1, "sentinel cleanup must delete only the removed row");
        var rows = await ReadHistoryRowsAsync(session.SessionId);
        rows.Select(row => row.Content).ShouldBe(["entry-0", "after"]);
        rows.Select(row => row.Id).ShouldBe(before.Where(row => row.Id != sentinelId).Select(row => row.Id));
    }

    [Fact]
    public async Task SaveAsync_ConcurrentDestructiveChangeAfterSnapshot_ReconcilesWithoutLossOrDuplicates()
    {
        var store = CreateStore();
        var session = await CreateSavedSessionAsync(store, "destructive-race", entryCount: 2);
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "captured" });
        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeHistoryWriteAsync = async (snapshot, cancellationToken) =>
        {
            snapshot.Entries.Select(entry => entry.Content).ShouldBe(["captured"]);
            snapshotCaptured.SetResult();
            await allowWrite.Task.WaitAsync(cancellationToken);
        };

        var firstSave = store.SaveAsync(session);
        await snapshotCaptured.Task;
        var replacement = session.GetHistorySnapshot()
            .Where(entry => entry.Content != "entry-0")
            .Append(new SessionEntry { Role = MessageRole.System, Content = "summary", IsCompactionSummary = true })
            .ToArray();
        session.ReplaceHistory(replacement);
        allowWrite.SetResult();
        await firstSave;

        store.BeforeHistoryWriteAsync = null;
        await store.SaveAsync(session);

        var rows = await ReadHistoryRowsAsync(session.SessionId);
        rows.Select(row => row.Content).ShouldBe(["entry-1", "captured", "summary"]);
        rows.Select(row => row.Content).Distinct().Count().ShouldBe(rows.Count);
    }

    [Fact]
    public async Task SaveAsync_DestructiveReconciliation_PreservesUnknownConcurrentDatabaseRows()
    {
        var store = CreateStore();
        var session = await CreateSavedSessionAsync(store, "external-append", entryCount: 2);
        var originalRows = await ReadHistoryRowsAsync(session.SessionId);
        var replacement = session.GetHistorySnapshot()
            .Where(entry => entry.Content != "entry-0")
            .ToArray();
        session.ReplaceHistory(replacement);

        await InsertExternalHistoryRowAsync(session.SessionId, "external");
        await store.SaveAsync(session);

        var rows = await ReadHistoryRowsAsync(session.SessionId);
        rows.Select(row => row.Content).ShouldBe(["entry-1", "external"]);
        rows.Single(row => row.Content == "entry-1").Id.ShouldBe(originalRows[1].Id);
        store.LastHistoryRowsMutated.ShouldBe(1, "only the row explicitly removed from this aggregate may be deleted");
    }

    [Fact]
    public async Task SaveAsync_ConcurrentAppendAfterSnapshot_RemainsPendingAndIsNotDuplicated()
    {
        var store = CreateStore();
        var session = await CreateSavedSessionAsync(store, "concurrent");
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "captured" });
        var snapshotCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeHistoryWriteAsync = async (snapshot, cancellationToken) =>
        {
            snapshot.Entries.Select(entry => entry.Content).ShouldBe(["captured"]);
            snapshotCaptured.SetResult();
            await allowWrite.Task.WaitAsync(cancellationToken);
        };

        var firstSave = store.SaveAsync(session);
        await snapshotCaptured.Task;
        session.AddEntry(new SessionEntry { Role = MessageRole.Assistant, Content = "raced" });
        allowWrite.SetResult();
        await firstSave;

        store.BeforeHistoryWriteAsync = null;
        await store.SaveAsync(session);

        store.LastHistoryRowsMutated.ShouldBe(1, "the first acknowledgement must leave the concurrent tail pending");
        var contents = (await ReadHistoryRowsAsync(session.SessionId)).Select(row => row.Content).ToList();
        contents.ShouldBe(["entry-0", "captured", "raced"]);
        contents.Count(content => content == "raced").ShouldBe(1);
    }

    private async Task InsertExternalHistoryRowAsync(SessionId sessionId, string content)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_history (session_id, role, content, timestamp)
            VALUES ($sessionId, 'assistant', $content, $timestamp)
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<(long Id, string Content, bool IsHistory)>> ReadHistoryRowsAsync(SessionId sessionId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, content, is_history FROM session_history WHERE session_id = $sessionId ORDER BY id";
        command.Parameters.AddWithValue("$sessionId", sessionId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<(long Id, string Content, bool IsHistory)>();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0));
        return rows;
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolForConnectionString(_connectionString);
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked handle on Windows must not fail the run.
        }
    }
}
