using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// P9-F (#657) back-compat tests for participant migration. Prior to P9-F, participants
/// were persisted on each session in <c>sessions.participants_json</c>. P9-F moves them
/// to the conversation-level <c>conversation_participants</c> table; the legacy column is
/// preserved for one phase as a rollback source. These tests verify:
/// <list type="bullet">
///   <item>The one-shot startup backfill forwards legacy <c>participants_json</c> into
///   the conversation store.</item>
///   <item><see cref="SqliteSessionStore.SaveAsync"/> no longer writes participants onto
///   the session — participants only land via
///   <see cref="IConversationStore.AddParticipantsAsync"/>.</item>
/// </list>
/// </summary>
public sealed class SqliteSessionStoreSessionParticipantBackCompatTests
{
    [Fact]
    public async Task EnsureCreatedAsync_WithLegacyParticipantJson_BackfillsConversationParticipants()
    {
        using var fixture = new StoreFixture();

        // Bootstrap the schema by opening the store once and saving a placeholder
        // session — this ensures all tables, columns, and migrations are in place
        // before we hand-inject legacy JSON.
        var bootstrap = fixture.CreateStore();
        var placeholder = await bootstrap.GetOrCreateAsync(SessionId.From("placeholder"), AgentId.From("agent-a"));
        await bootstrap.SaveAsync(placeholder);

        var conversationId = placeholder.ConversationId;
        conversationId.IsInitialized().ShouldBeTrue("placeholder save should have stamped a legacy conversation id");

        const string legacyParticipantsJson = """
            [
              { "citizenId": { "kind": "User", "id": "alice" }, "role": "caller" },
              { "citizenId": { "kind": "Agent", "id": "agent-b" }, "role": "target" }
            ]
            """;

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions
                  (id, agent_id, channel_type, caller_id, session_type, participants_json, status, metadata, created_at, updated_at, conversation_id)
                VALUES
                  ($id, $agentId, NULL, NULL, $sessionType, $participants, $status, '{}', $createdAt, $updatedAt, $convId)
                """;
            insert.Parameters.AddWithValue("$id", "legacy-session");
            insert.Parameters.AddWithValue("$agentId", "agent-a");
            insert.Parameters.AddWithValue("$sessionType", SessionType.UserAgent.Value);
            insert.Parameters.AddWithValue("$participants", legacyParticipantsJson);
            insert.Parameters.AddWithValue("$status", SessionStatus.Active.ToString());
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$convId", conversationId.Value);
            await insert.ExecuteNonQueryAsync();
        }

        // Re-open the store: this re-runs EnsureCreatedAsync which now also triggers
        // the participant backfill scan. The backfill must forward our hand-injected
        // legacy participants_json into the conversation_participants table.
        var reopened = fixture.CreateStore();
        await reopened.GetOrCreateAsync(SessionId.From("trigger-startup"), AgentId.From("agent-a"));

        var conversation = await fixture.Conversations.GetAsync(conversationId);
        conversation.ShouldNotBeNull();
        conversation!.Participants.Count.ShouldBe(2);

        var caller = conversation.Participants.Single(p => p.Role == "caller");
        caller.CitizenId.Kind.ShouldBe(CitizenKind.User);
        caller.CitizenId.AsUser!.Value.Value.ShouldBe("alice");

        var target = conversation.Participants.Single(p => p.Role == "target");
        target.CitizenId.Kind.ShouldBe(CitizenKind.Agent);
        target.CitizenId.AsAgent!.Value.Value.ShouldBe("agent-b");
    }

    [Fact]
    public async Task SaveAsync_DoesNotWriteParticipantsOnSession_P9F()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        // P9-F: producers route participant adds through IConversationStore.
        var session = await store.GetOrCreateAsync(SessionId.From("post-p9f"), AgentId.From("agent-a"));
        await store.SaveAsync(session);

        await fixture.Conversations.AddParticipantsAsync(
            session.ConversationId,
            [
                new SessionParticipant
                {
                    CitizenId = CitizenId.Of(UserId.From("alice")),
                    Role = "caller",
                }
            ]);

        string? participantsJson;
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT participants_json FROM sessions WHERE id = $id";
            read.Parameters.AddWithValue("$id", "post-p9f");
            participantsJson = (string?)await read.ExecuteScalarAsync();
        }

        // SaveAsync now writes an empty array so the backfill scan's "non-empty" filter
        // excludes new rows. Concrete value is "[]" (not null, not stale data).
        participantsJson.ShouldBe("[]");

        // The conversation now owns the participant set.
        var conversation = await fixture.Conversations.GetAsync(session.ConversationId);
        conversation.ShouldNotBeNull();
        conversation!.Participants.ShouldHaveSingleItem()
            .CitizenId.AsUser!.Value.Value.ShouldBe("alice");
    }

    [Fact]
    public async Task EnsureCreatedAsync_RunTwice_BackfillIsIdempotent()
    {
        using var fixture = new StoreFixture();

        // Same bootstrap as the first test: open once, save a placeholder so the schema
        // is materialised, then hand-inject legacy participants_json. Use a stable
        // CitizenId so re-running the backfill cannot create a "duplicate" row that
        // looks legitimately new under any race interpretation.
        var bootstrap = fixture.CreateStore();
        var placeholder = await bootstrap.GetOrCreateAsync(SessionId.From("placeholder"), AgentId.From("agent-a"));
        await bootstrap.SaveAsync(placeholder);

        var conversationId = placeholder.ConversationId;
        const string legacyParticipantsJson = """
            [
              { "citizenId": { "kind": "User", "id": "alice" }, "role": "caller" }
            ]
            """;

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions
                  (id, agent_id, channel_type, caller_id, session_type, participants_json, status, metadata, created_at, updated_at, conversation_id)
                VALUES
                  ($id, $agentId, NULL, NULL, $sessionType, $participants, $status, '{}', $createdAt, $updatedAt, $convId)
                """;
            insert.Parameters.AddWithValue("$id", "legacy-session");
            insert.Parameters.AddWithValue("$agentId", "agent-a");
            insert.Parameters.AddWithValue("$sessionType", SessionType.UserAgent.Value);
            insert.Parameters.AddWithValue("$participants", legacyParticipantsJson);
            insert.Parameters.AddWithValue("$status", SessionStatus.Active.ToString());
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$convId", conversationId.Value);
            await insert.ExecuteNonQueryAsync();
        }

        // Re-open the store TWICE in succession — each EnsureCreatedAsync runs the
        // backfill scan. The legacy row still has participants_json set (we never
        // clear it; it stays declared as a rollback source per the P9-F design), so the
        // scan picks it up on both passes. AddParticipantsAsync MUST be idempotent.
        var first = fixture.CreateStore();
        await first.GetOrCreateAsync(SessionId.From("trigger-startup-1"), AgentId.From("agent-a"));

        var second = fixture.CreateStore();
        await second.GetOrCreateAsync(SessionId.From("trigger-startup-2"), AgentId.From("agent-a"));

        // After two passes there must still be exactly one participant — not two,
        // not zero. Idempotence is the contract.
        var conversation = await fixture.Conversations.GetAsync(conversationId);
        conversation.ShouldNotBeNull();
        conversation!.Participants.Count.ShouldBe(
            1,
            "P9-F: backfill idempotence — re-running EnsureCreatedAsync must not duplicate participants. " +
            "If this fails, the backfill is not using INSERT OR IGNORE / dedupe-by-citizen.");
        conversation.Participants[0].CitizenId.AsUser!.Value.Value.ShouldBe("alice");
    }

    private sealed class StoreFixture : IDisposable
    {
        public StoreFixture()
        {
            DirectoryPath = Path.Combine(
                AppContext.BaseDirectory,
                "SqliteSessionStoreSessionParticipantBackCompatTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            DatabasePath = Path.Combine(DirectoryPath, "sessions.db");
            ConnectionString = $"Data Source={DatabasePath};Pooling=False";
            Conversations = new InMemoryConversationStore();
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string ConnectionString { get; }
        public InMemoryConversationStore Conversations { get; }

        public SqliteSessionStore CreateStore(IConversationStore? conversationStore = null)
            => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance, conversationStore ?? Conversations);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
