using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the <c>GET /api/sessions/{sessionId}/subagents/history</c> endpoint (Issue #809, Part of #785).
/// </summary>
public sealed class SubAgentSessionHistoryTests : IDisposable
{
    private readonly string _directoryPath;
    private readonly SqliteSessionStore _store;
    private readonly string _sessionDbPath;
    private readonly string _conversationDbPath;

    public SubAgentSessionHistoryTests()
    {
        _directoryPath = Path.Combine(
            AppContext.BaseDirectory,
            "SubAgentSessionHistoryTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
        _sessionDbPath      = Path.Combine(_directoryPath, "sessions.db");
        _conversationDbPath = Path.Combine(_directoryPath, "conversations.db");

        var convStore = new SqliteConversationStore(
            $"Data Source={_conversationDbPath};Pooling=False",
            NullLogger<SqliteConversationStore>.Instance);
        _store = new SqliteSessionStore(
            $"Data Source={_sessionDbPath};Pooling=False",
            NullLogger<SqliteSessionStore>.Instance,
            convStore);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directoryPath))
            Directory.Delete(_directoryPath, recursive: true);
    }

    // ── store-level tests ──────────────────────────────────────────────────

    [Fact]
    public async Task ListSubAgentSessionsAsync_WithNoRows_ReturnsEmpty()
    {
        // Trigger schema creation
        await _store.GetAsync(SessionId.From("nonexistent"));

        var result = await _store.ListSubAgentSessionsAsync(SessionId.From("s-parent-1"));

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListSubAgentSessionsAsync_WithMatchingRows_ReturnsSummaries()
    {
        await _store.GetAsync(SessionId.From("nonexistent")); // trigger schema

        await SeedSubAgentRowAsync("sub-1", "s-parent-2", "agent-a", "agent-b", "researcher",
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-1), "Completed");
        await SeedSubAgentRowAsync("sub-2", "s-parent-2", "agent-a", "agent-c", null,
            DateTimeOffset.UtcNow.AddMinutes(-3), null, "Active");

        var result = await _store.ListSubAgentSessionsAsync(SessionId.From("s-parent-2"));

        result.Count.ShouldBe(2);
        result[0].SubAgentId.ShouldBe("sub-1");
        result[0].Archetype.ShouldBe("researcher");
        result[0].Status.ShouldBe("Completed");
        result[0].EndedAt.ShouldNotBeNull();
        result[1].SubAgentId.ShouldBe("sub-2");
        result[1].Archetype.ShouldBeNull();
        result[1].Status.ShouldBe("Active");
        result[1].EndedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ListSubAgentSessionsAsync_OnlyReturnsRowsForRequestedSession()
    {
        await _store.GetAsync(SessionId.From("nonexistent")); // trigger schema

        await SeedSubAgentRowAsync("sub-3", "s-parent-3", "agent-a", "agent-b", null,
            DateTimeOffset.UtcNow.AddMinutes(-2), null, "Active");
        await SeedSubAgentRowAsync("sub-4", "s-parent-X", "agent-a", "agent-b", null,
            DateTimeOffset.UtcNow.AddMinutes(-2), null, "Active");

        var result = await _store.ListSubAgentSessionsAsync(SessionId.From("s-parent-3"));

        result.Count.ShouldBe(1);
        result[0].SubAgentId.ShouldBe("sub-3");
    }

    // ── controller-level tests ─────────────────────────────────────────────

    [Fact]
    public async Task GetSubAgentHistory_WithUnknownSession_ReturnsNotFound()
    {
        var controller = new SessionsController(_store);
        var actionResult = await controller.GetSubAgentHistory("does-not-exist", CancellationToken.None);
        actionResult.Result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetSubAgentHistory_WithKnownSession_ReturnsOkWithHistory()
    {
        // Arrange: create a real session so the controller's 404 guard passes.
        // Use InMemorySessionStore for the controller test to avoid SQLite WAL seed isolation.
        var inMemStore = new InMemorySessionStore();
        await inMemStore.GetOrCreateAsync(SessionId.From("s-ctrl-5"), AgentId.From("agent-a"));

        // The InMemorySessionStore returns the interface default (empty list) for
        // ListSubAgentSessionsAsync -- that is expected behaviour for non-SQLite stores.
        // This test verifies the controller returns 200 OK with the store's result.
        var controller = new SessionsController(inMemStore);
        var actionResult = await controller.GetSubAgentHistory("s-ctrl-5", CancellationToken.None);

        var ok = actionResult.Result.ShouldBeOfType<OkObjectResult>();
        var summaries = ok.Value.ShouldBeAssignableTo<IReadOnlyList<SubAgentSessionSummary>>();
        summaries!.ShouldNotBeNull();
        // InMemorySessionStore has no sub_agent_sessions persistence -- result is empty (correct)
        summaries.ShouldBeEmpty();
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task SeedSubAgentRowAsync(
        string id,
        string parentSessionId,
        string parentAgentId,
        string childAgentId,
        string? archetype,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt,
        string status)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_sessionDbPath};Pooling=False");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sub_agent_sessions
                (id, parent_session_id, parent_agent_id, child_agent_id, archetype, started_at, ended_at, status)
            VALUES
                ($id, $parentSessionId, $parentAgentId, $childAgentId, $archetype, $startedAt, $endedAt, $status)
            """;
        cmd.Parameters.AddWithValue("$id",              id);
        cmd.Parameters.AddWithValue("$parentSessionId", parentSessionId);
        cmd.Parameters.AddWithValue("$parentAgentId",   parentAgentId);
        cmd.Parameters.AddWithValue("$childAgentId",    childAgentId);
        cmd.Parameters.AddWithValue("$archetype",       (object?)archetype ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$startedAt",       startedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$endedAt",         endedAt.HasValue ? (object)endedAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$status",          status);
        await cmd.ExecuteNonQueryAsync();
    }
}
