using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Tests for the read-only platform-wide sub-agent observability surface (Issue #1941):
/// the store-level <see cref="SqliteSessionStore.ListAllSubAgentSessionsAsync"/> read and the
/// <c>GET /api/subagents</c> endpoint exposed by <see cref="SubAgentsController"/>.
/// </summary>
public sealed class SubAgentObservabilityTests : IDisposable
{
    private readonly string _directoryPath;
    private readonly SqliteSessionStore _store;
    private readonly string _sessionDbPath;

    public SubAgentObservabilityTests()
    {
        _directoryPath = Path.Combine(
            AppContext.BaseDirectory,
            "SubAgentObservabilityTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directoryPath);
        _sessionDbPath = Path.Combine(_directoryPath, "sessions.db");
        var conversationDbPath = Path.Combine(_directoryPath, "conversations.db");

        var convStore = new SqliteConversationStore(
            $"Data Source={conversationDbPath};Pooling=False",
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
    public async Task ListAllSubAgentSessionsAsync_WithNoRows_ReturnsEmpty()
    {
        await _store.GetAsync(SessionId.From("nonexistent")); // trigger schema

        var result = await _store.ListAllSubAgentSessionsAsync();

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListAllSubAgentSessionsAsync_ReturnsRowsAcrossParents_NewestFirst()
    {
        await _store.GetAsync(SessionId.From("nonexistent")); // trigger schema

        await SeedSubAgentRowAsync("sub-1", "s-parent-1", "agent-a", "agent-b", "researcher",
            DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(-9), "Completed");
        await SeedSubAgentRowAsync("sub-2", "s-parent-2", "agent-a", "agent-c", null,
            DateTimeOffset.UtcNow.AddMinutes(-2), null, "Active");

        var result = await _store.ListAllSubAgentSessionsAsync();

        result.Count.ShouldBe(2);
        // Newest started_at first for an observability feed.
        result[0].SubAgentId.ShouldBe("sub-2");
        result[1].SubAgentId.ShouldBe("sub-1");
    }

    [Fact]
    public async Task ListAllSubAgentSessionsAsync_WithStatusFilter_ReturnsOnlyMatching()
    {
        await _store.GetAsync(SessionId.From("nonexistent")); // trigger schema

        await SeedSubAgentRowAsync("sub-c", "s-parent-1", "agent-a", "agent-b", null,
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4), "Completed");
        await SeedSubAgentRowAsync("sub-f", "s-parent-1", "agent-a", "agent-b", null,
            DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow.AddMinutes(-2), "Failed");

        var result = await _store.ListAllSubAgentSessionsAsync("Failed");

        result.Count.ShouldBe(1);
        result[0].SubAgentId.ShouldBe("sub-f");
        result[0].Status.ShouldBe("Failed");
    }

    [Fact]
    public async Task ListAllSubAgentSessionsAsync_RespectsLimit()
    {
        await _store.GetAsync(SessionId.From("nonexistent")); // trigger schema

        for (var i = 0; i < 5; i++)
        {
            await SeedSubAgentRowAsync($"sub-{i}", "s-parent-1", "agent-a", "agent-b", null,
                DateTimeOffset.UtcNow.AddMinutes(-i), null, "Active");
        }

        var result = await _store.ListAllSubAgentSessionsAsync(status: null, limit: 2);

        result.Count.ShouldBe(2);
    }

    // ── controller-level tests ─────────────────────────────────────────────

    [Fact]
    public async Task List_WithNoSubAgents_ReturnsOkEmpty()
    {
        await _store.GetAsync(SessionId.From("nonexistent")); // trigger schema
        var controller = new SubAgentsController(_store);

        var actionResult = await controller.List(status: null, limit: 200, CancellationToken.None);

        var ok = actionResult.Result.ShouldBeOfType<OkObjectResult>();
        var summaries = ok.Value.ShouldBeAssignableTo<IReadOnlyList<SubAgentSessionSummary>>();
        summaries!.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_WithSubAgents_ReturnsOkWithRows()
    {
        await _store.GetAsync(SessionId.From("nonexistent")); // trigger schema
        await SeedSubAgentRowAsync("sub-1", "s-parent-1", "agent-a", "agent-b", "coder",
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-1), "Completed");
        var controller = new SubAgentsController(_store);

        var actionResult = await controller.List(status: null, limit: 200, CancellationToken.None);

        var ok = actionResult.Result.ShouldBeOfType<OkObjectResult>();
        var summaries = ok.Value.ShouldBeAssignableTo<IReadOnlyList<SubAgentSessionSummary>>();
        summaries!.Count.ShouldBe(1);
        summaries[0].SubAgentId.ShouldBe("sub-1");
    }

    [Fact]
    public async Task List_WithStatusFilter_ReturnsOnlyMatching()
    {
        await _store.GetAsync(SessionId.From("nonexistent")); // trigger schema
        await SeedSubAgentRowAsync("sub-c", "s-parent-1", "agent-a", "agent-b", null,
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4), "Completed");
        await SeedSubAgentRowAsync("sub-k", "s-parent-1", "agent-a", "agent-b", null,
            DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow.AddMinutes(-2), "Killed");
        var controller = new SubAgentsController(_store);

        var actionResult = await controller.List(status: "Killed", limit: 200, CancellationToken.None);

        var ok = actionResult.Result.ShouldBeOfType<OkObjectResult>();
        var summaries = ok.Value.ShouldBeAssignableTo<IReadOnlyList<SubAgentSessionSummary>>();
        summaries!.Count.ShouldBe(1);
        summaries[0].SubAgentId.ShouldBe("sub-k");
    }

    [Fact]
    public async Task List_WithInvalidLimit_ReturnsBadRequest()
    {
        var controller = new SubAgentsController(_store);

        var actionResult = await controller.List(status: null, limit: 0, CancellationToken.None);

        actionResult.Result.ShouldBeOfType<BadRequestObjectResult>();
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
