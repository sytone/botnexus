using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Regression tests pinning the P9-I (#674) contract: the legacy <c>sessions.agent_id</c>
/// column and its two associated indexes are removed; AgentId is hydrated on load from
/// <c>Conversation.AgentId</c>. Each test covers one specific failure mode that, if it
/// regressed silently, would be hard to spot in normal end-to-end tests.
/// </summary>
public sealed class SessionAgentIdRemovalRegressionTests : IDisposable
{
    private readonly string _directoryPath;
    private readonly string _sessionDbPath;
    private readonly string _conversationDbPath;

    public SessionAgentIdRemovalRegressionTests()
    {
        _directoryPath = Path.Combine(
            AppContext.BaseDirectory,
            "SessionAgentIdRemovalRegressionTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
        _sessionDbPath = Path.Combine(_directoryPath, "sessions.db");
        _conversationDbPath = Path.Combine(_directoryPath, "conversations.db");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
            Directory.Delete(_directoryPath, recursive: true);
    }

    // ─── Fresh DB schema ─────────────────────────────────────────────────────

    [Fact]
    public async Task FreshDatabase_SchemaHas_NoAgentIdColumn_OnSessionsTable()
    {
        // Regression pin: opening SqliteSessionStore on an EMPTY path must produce a
        // sessions table that has never carried the agent_id column. If a future schema
        // change re-adds it, this test catches it before any data is written.
        var convStore = new SqliteConversationStore(
            $"Data Source={_conversationDbPath};Pooling=False",
            NullLogger<SqliteConversationStore>.Instance);
        var sessionStore = new SqliteSessionStore(
            $"Data Source={_sessionDbPath};Pooling=False",
            NullLogger<SqliteSessionStore>.Instance,
            convStore);

        // Trigger EnsureCreatedAsync.
        _ = await sessionStore.GetAsync(SessionId.From("probe"));

        await using var connection = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False");
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(sessions)";
        var columns = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));
        }

        columns.ShouldNotBeEmpty("sessions table must exist after EnsureCreatedAsync");
        columns.ShouldNotContain(
            "agent_id",
            "P9-I (#674): fresh DBs must have NO agent_id column — AgentId now lives on " +
            "Conversation.AgentId and is hydrated on load. If this test fails, a schema " +
            "change accidentally re-added the legacy column.");
        // Pin the columns we DO expect, to catch accidental removals (defensive).
        columns.ShouldContain("id");
        columns.ShouldContain("conversation_id");
    }

    // ─── Pre-P9 DB legacy-index drop ─────────────────────────────────────────

    [Fact]
    public async Task PreP9Database_WithLegacyIndexes_HasIndexesDropped_OnOpen()
    {
        // Regression pin: a pre-P9 SQLite database carries the legacy composite index
        // `idx_sessions_conversation_agent` AND the single-column index `idx_sessions_agent_id`.
        // Opening the post-P9-I store must drop BOTH and create the replacement
        // `idx_sessions_conversation_created`. If the DROP INDEX statements regress, the
        // legacy indexes silently linger and waste space / mislead query planners.
        var connString = $"Data Source={_sessionDbPath};Pooling=False";
        await using (var seed = new SqliteConnection(connString))
        {
            await seed.OpenAsync();
            await using var seedCmd = seed.CreateCommand();
            seedCmd.CommandText = """
                CREATE TABLE sessions (
                    id TEXT PRIMARY KEY,
                    agent_id TEXT,
                    channel_type TEXT,
                    caller_id TEXT,
                    session_type TEXT,
                    participants_json TEXT,
                    status TEXT,
                    metadata TEXT,
                    created_at TEXT,
                    updated_at TEXT,
                    conversation_id TEXT
                );
                CREATE INDEX idx_sessions_agent_id ON sessions(agent_id);
                CREATE INDEX idx_sessions_conversation_agent ON sessions(conversation_id, agent_id);
                """;
            await seedCmd.ExecuteNonQueryAsync();
        }

        // Act: open the store; EnsureCreatedAsync runs the schema sweep + DropLegacyAgentIdColumnAsync.
        var convStore = new SqliteConversationStore(
            $"Data Source={_conversationDbPath};Pooling=False",
            NullLogger<SqliteConversationStore>.Instance);
        var sessionStore = new SqliteSessionStore(
            connString,
            NullLogger<SqliteSessionStore>.Instance,
            convStore);
        _ = await sessionStore.GetAsync(SessionId.From("probe"));

        // Assert: legacy indexes are gone, replacement index is present.
        await using var verify = new SqliteConnection(connString);
        await verify.OpenAsync();
        await using var cmd = verify.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'sessions'";
        var indexes = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                indexes.Add(reader.GetString(0));
        }

        indexes.ShouldNotContain(
            "idx_sessions_agent_id",
            "Legacy single-column index references the dropped agent_id column — must be removed on open.");
        indexes.ShouldNotContain(
            "idx_sessions_conversation_agent",
            "Legacy composite index references the dropped agent_id column — must be removed on open.");
        indexes.ShouldContain(
            "idx_sessions_conversation_created",
            "P9-I replacement index for ListByConversationAsync ordering must be created.");
    }

    // ─── Missing-conversation error ──────────────────────────────────────────

    [Fact]
    public async Task LoadingSession_WithDanglingConversationId_Throws_InvalidOperationException()
    {
        // Regression pin: post-P9-I, AgentId is hydrated from Conversation.AgentId. If a
        // session row's conversation_id points at a conversation that doesn't exist (e.g.
        // because it was deleted while the session row remained), HydrateAgentIdAsync MUST
        // fail loudly rather than silently produce a session with default(AgentId).
        var convStore = new SqliteConversationStore(
            $"Data Source={_conversationDbPath};Pooling=False",
            NullLogger<SqliteConversationStore>.Instance);
        var sessionStore = new SqliteSessionStore(
            $"Data Source={_sessionDbPath};Pooling=False",
            NullLogger<SqliteSessionStore>.Instance,
            convStore);
        // Initialise schema.
        _ = await sessionStore.GetAsync(SessionId.From("init"));

        // Inject a session row whose conversation_id points at a missing conversation.
        const string danglingConvId = "conv-deleted";
        await using (var seed = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False"))
        {
            await seed.OpenAsync();
            await using var insert = seed.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions (id, channel_type, caller_id, session_type, participants_json,
                                       status, metadata, created_at, updated_at, conversation_id)
                VALUES ('s-dangling', 'test', NULL, 'standard', '[]',
                        'Active', '{}', $createdAt, $updatedAt, $convId)
                """;
            var now = DateTimeOffset.UtcNow.ToString("O");
            insert.Parameters.AddWithValue("$createdAt", now);
            insert.Parameters.AddWithValue("$updatedAt", now);
            insert.Parameters.AddWithValue("$convId", danglingConvId);
            await insert.ExecuteNonQueryAsync();
        }

        // Act + Assert: loading the dangling session throws.
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await sessionStore.GetAsync(SessionId.From("s-dangling")));

        ex.Message.ShouldContain("AgentId cannot be hydrated",
            customMessage: "Error must explicitly state hydration failed so operators can " +
                           "identify the post-P9-I dangling-conversation failure mode.");
        ex.Message.ShouldContain(danglingConvId,
            customMessage: "Error must reference the offending conversation id for diagnosis.");
        ex.Message.ShouldContain("s-dangling",
            customMessage: "Error must reference the session id so operators can find the row.");
    }

    // ─── Sidecar legacy migration ────────────────────────────────────────────

    [Fact]
    public async Task FileSessionStore_OnOpen_RewritesLegacySidecar_StampingConversationAndNullingAgentId()
    {
        // Regression pin: a pre-P9 file sidecar carries `agentId` but no `conversationId`.
        // On the first store open the migration sweep must:
        //   1. resolve the legacy conversation for that agentId,
        //   2. rewrite the sidecar with the stamped conversationId,
        //   3. write agentId as null (the post-P9-I shape — durable AgentId lives on the
        //      Conversation, not the session sidecar).
        var fileSystem = new MockFileSystem();
        var storePath = Path.Combine(Path.GetTempPath(), "P9I-sidecar-regression", Guid.NewGuid().ToString("N"));
        fileSystem.Directory.CreateDirectory(storePath);

        const string sessionId = "s-legacy";
        var sidecarPath = Path.Combine(storePath, $"{sessionId}.meta.json");
        var historyPath = Path.Combine(storePath, $"{sessionId}.jsonl");

        // Seed a legacy sidecar — agentId set, conversationId absent — mimicking the pre-P9
        // disk shape. Property names match the JSON wire shape (camelCase). The agentId
        // value "agent-legacy" is what MigrateOrphanedSessionsAsync reads to resolve the
        // legacy:agent-legacy conversation; it must round-trip into the sidecar's stamp.
        const string legacySidecar = """
            {
              "agentId": "agent-legacy",
              "channelType": null,
              "callerId": null,
              "sessionType": "Standard",
              "participants": [],
              "createdAt": "2024-01-01T00:00:00+00:00",
              "updatedAt": "2024-01-01T00:00:00+00:00",
              "status": "Active",
              "nextSequenceId": 1
            }
            """;
        fileSystem.File.WriteAllText(sidecarPath, legacySidecar);
        // Empty history file is required so the store can load the session.
        fileSystem.File.WriteAllText(historyPath, string.Empty);

        // Act: open the store. EnsureMigratedAsync runs MigrateOrphanedSessionsAsync, which
        // rewrites every orphan sidecar in place.
        var convStore = new InMemoryConversationStore();
        var store = new FileSessionStore(
            storePath,
            NullLogger<FileSessionStore>.Instance,
            fileSystem,
            convStore);
        // Trigger init by performing any operation (here a List).
        _ = await store.ListAsync();

        // Assert: the sidecar was rewritten with conversationId stamped and agentId == null.
        var rewritten = fileSystem.File.ReadAllText(sidecarPath);
        using var doc = JsonDocument.Parse(rewritten);
        var root = doc.RootElement;

        root.TryGetProperty("conversationId", out var convProp).ShouldBeTrue(
            "Rewritten sidecar must carry a conversationId post-migration.");
        convProp.ValueKind.ShouldBe(JsonValueKind.String,
            "conversationId must be a string after the migration stamps the legacy conversation.");
        convProp.GetString().ShouldNotBeNullOrEmpty(
            "Stamped conversationId must be non-empty.");

        root.TryGetProperty("agentId", out var agentProp).ShouldBeTrue(
            "Sidecar still carries the agentId property for back-compat reads.");
        agentProp.ValueKind.ShouldBe(JsonValueKind.Null,
            "P9-I (#674): post-migration sidecars MUST write agentId as JSON null — " +
            "durable agent ownership has moved to Conversation.AgentId. The legacy " +
            "property stays for read-time recovery only; new writes must NOT populate it.");

        // Cleanup
        if (fileSystem.Directory.Exists(storePath))
            fileSystem.Directory.Delete(storePath, recursive: true);
    }
}
