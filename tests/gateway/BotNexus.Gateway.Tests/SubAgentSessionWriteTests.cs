using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Verifies that SaveSubAgentSessionAsync and UpdateSubAgentSessionAsync
/// correctly write and update rows in the sub_agent_sessions table (Issue #808, Part of #785).
/// </summary>
public sealed class SubAgentSessionWriteTests : IDisposable
{
    private readonly string _directoryPath;
    private readonly string _sessionDbPath;
    private readonly string _conversationDbPath;

    public SubAgentSessionWriteTests()
    {
        _directoryPath = Path.Combine(
            AppContext.BaseDirectory,
            "SubAgentSessionWriteTests",
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

    [Fact]
    public async Task SaveSubAgentSessionAsync_InsertsRow_WithActiveStatus()
    {
        var store = CreateSessionStore();
        // Initialise DB
        await store.GetAsync(SessionId.From("init"));

        var info = BuildInfo("sa-1", "parent-s1", "child-s1", SubAgentStatus.Running);

        await store.SaveSubAgentSessionAsync(info);

        var (status, endedAt) = await ReadRow("sa-1");
        status.ShouldBe("Active");
        endedAt.ShouldBeNull("ended_at should be NULL when sub-agent is spawned");
    }

    [Fact]
    public async Task UpdateSubAgentSessionAsync_SetsEndedAtAndStatus()
    {
        var store = CreateSessionStore();
        await store.GetAsync(SessionId.From("init"));

        var info = BuildInfo("sa-2", "parent-s2", "child-s2", SubAgentStatus.Running);
        await store.SaveSubAgentSessionAsync(info);

        var endedAt = new DateTimeOffset(2026, 6, 6, 12, 0, 0, TimeSpan.Zero);
        await store.UpdateSubAgentSessionAsync("sa-2", endedAt, "Completed");

        var (status, endedAtValue) = await ReadRow("sa-2");
        status.ShouldBe("Completed");
        endedAtValue.ShouldNotBeNull("ended_at should be set after update");
        endedAtValue.ShouldContain("2026-06-06");
    }

    [Fact]
    public async Task SaveSubAgentSessionAsync_IdempotentOnDuplicate()
    {
        var store = CreateSessionStore();
        await store.GetAsync(SessionId.From("init"));

        var info = BuildInfo("sa-3", "parent-s3", "child-s3", SubAgentStatus.Running);
        await store.SaveSubAgentSessionAsync(info);
        // Second call with the same id should be silently ignored (INSERT OR IGNORE).
        var act = async () => await store.SaveSubAgentSessionAsync(info);
        await act.ShouldNotThrowAsync();
    }

    [Fact]
    public async Task UpdateSubAgentSessionAsync_NoRowForId_DoesNotThrow()
    {
        var store = CreateSessionStore();
        await store.GetAsync(SessionId.From("init"));

        // Updating a non-existent row should be a silent no-op.
        var act = async () => await store.UpdateSubAgentSessionAsync(
            "nonexistent-sa", DateTimeOffset.UtcNow, "Completed");
        await act.ShouldNotThrowAsync();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private SqliteSessionStore CreateSessionStore()
    {
        var convStore = new SqliteConversationStore(
            $"Data Source={_conversationDbPath};Pooling=False",
            NullLogger<SqliteConversationStore>.Instance);
        return new SqliteSessionStore(
            $"Data Source={_sessionDbPath};Pooling=False",
            NullLogger<SqliteSessionStore>.Instance,
            convStore);
    }

    private static SubAgentInfo BuildInfo(
        string subAgentId,
        string parentSessionId,
        string childSessionId,
        SubAgentStatus status)
        => new SubAgentInfo
        {
            SubAgentId = subAgentId,
            ParentSessionId = SessionId.From(parentSessionId),
            ChildSessionId = SessionId.From(childSessionId),
            Name = null,
            ParentAgentId = "parent-agent-a",
            ChildAgentId = "child-agent-a",
            Task = "test task",
            Archetype = SubAgentArchetype.General,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow
        };

    private async Task<(string? status, string? endedAt)> ReadRow(string subAgentId)
    {
        await using var conn = new SqliteConnection($"Data Source={_sessionDbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status, ended_at FROM sub_agent_sessions WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", subAgentId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (null, null);
        return (
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1)
        );
    }
}
