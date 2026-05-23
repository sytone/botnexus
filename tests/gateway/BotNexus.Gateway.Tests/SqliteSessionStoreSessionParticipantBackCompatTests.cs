using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Back-compat integration tests for <see cref="SessionParticipant"/> persistence.
/// Verifies that sessions written before Phase 1.5 (legacy <c>{type,id,worldId}</c> shape)
/// still deserialise into the new CitizenId-based shape when re-read by the current store.
/// </summary>
public sealed class SqliteSessionStoreSessionParticipantBackCompatTests
{
    [Fact]
    public async Task GetAsync_WithLegacyParticipantJson_DeserializesAsCitizenId()
    {
        using var fixture = new StoreFixture();

        // Bootstrap the schema by opening the store once and saving a placeholder
        // session — this ensures all tables, columns, and migrations are in place
        // before we hand-inject legacy JSON.
        var bootstrap = fixture.CreateStore();
        var placeholder = await bootstrap.GetOrCreateAsync(SessionId.From("placeholder"), AgentId.From("agent-a"));
        await bootstrap.SaveAsync(placeholder);

        const string legacyParticipantsJson = """
            [
              { "type": "User", "id": "alice", "worldId": "world-a", "role": "caller" },
              { "type": 1, "id": "agent-b", "worldId": "world-b", "role": "target" }
            ]
            """;

        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions
                  (id, agent_id, channel_type, caller_id, session_type, participants_json, status, metadata, created_at, updated_at)
                VALUES
                  ($id, $agentId, NULL, NULL, $sessionType, $participants, $status, '{}', $createdAt, $updatedAt)
                """;
            insert.Parameters.AddWithValue("$id", "legacy-session");
            insert.Parameters.AddWithValue("$agentId", "agent-a");
            insert.Parameters.AddWithValue("$sessionType", SessionType.UserAgent.Value);
            insert.Parameters.AddWithValue("$participants", legacyParticipantsJson);
            insert.Parameters.AddWithValue("$status", SessionStatus.Active.ToString());
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }

        var reloaded = await fixture.CreateStore().GetAsync(SessionId.From("legacy-session"));

        reloaded.ShouldNotBeNull();
        reloaded!.Participants.Count.ShouldBe(2);

        var caller = reloaded.Participants[0];
        caller.CitizenId.Kind.ShouldBe(CitizenKind.User);
        caller.CitizenId.AsUser!.Value.Value.ShouldBe("alice");
        caller.Role.ShouldBe("caller");

        var target = reloaded.Participants[1];
        target.CitizenId.Kind.ShouldBe(CitizenKind.Agent);
        target.CitizenId.AsAgent!.Value.Value.ShouldBe("agent-b");
        target.Role.ShouldBe("target");
    }

    [Fact]
    public async Task SaveAsync_PersistsParticipants_InDualShape_ForRollbackSafety()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();

        var session = await store.GetOrCreateAsync(SessionId.From("dual-shape"), AgentId.From("agent-a"));
        session.Participants.Add(new SessionParticipant
        {
            CitizenId = CitizenId.Of(UserId.From("alice")),
            Role = "caller",
        });
        await store.SaveAsync(session);

        string? participantsJson;
        await using (var connection = new SqliteConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT participants_json FROM sessions WHERE id = $id";
            read.Parameters.AddWithValue("$id", "dual-shape");
            participantsJson = (string?)await read.ExecuteScalarAsync();
        }

        participantsJson.ShouldNotBeNull();
        participantsJson!.ShouldContain("\"citizenId\"");
        participantsJson.ShouldContain("\"type\":\"User\"");
        participantsJson.ShouldContain("\"id\":\"alice\"");
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
        }

        public string DirectoryPath { get; }
        public string DatabasePath { get; }
        public string ConnectionString { get; }

        public SqliteSessionStore CreateStore()
            => new(ConnectionString, NullLogger<SqliteSessionStore>.Instance);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
                Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
