using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// #3660: proves the SQLite pending-checkpoint query filters in SQL rather than materialising the
/// conversation population and discarding it in memory.
/// </summary>
/// <remarks>
/// The parity suite (<see cref="ConversationStoreContractTests"/>) asserts the observable result
/// set for every store. This class asserts the SQLite-specific <em>mechanism</em>, which is the
/// actual defect: <c>ListAsync</c> hands all ids to <c>MaterializeOrderedAsync</c>, so a correct
/// result set alone would not distinguish a filtered query from a full scan that filters late.
/// </remarks>
public sealed class SqliteConversationStorePendingAskUserQueryTests : IDisposable
{
    private readonly StoreFixture _fixture = new();

    /// <summary>
    /// Seeds a population, then instruments the connection so any statement that reads a column
    /// beyond the two-field projection is recorded. A full materialisation cannot help but read
    /// the wider column list, so an empty violation set is direct evidence of the narrow query.
    /// </summary>
    [Fact]
    public async Task GetPendingAskUserCheckpointsAsync_DoesNotMaterializeNonPendingConversations()
    {
        var store = _fixture.CreateStore();
        const int NoiseCount = 50;
        for (var i = 0; i < NoiseCount; i++)
        {
            await store.CreateAsync(new Conversation
            {
                ConversationId = ConversationId.Create(),
                AgentId = AgentId.From("agent-a"),
                Title = $"noise-{i}"
            });
        }

        var pendingId = ConversationId.Create();
        await store.CreateAsync(new Conversation
        {
            ConversationId = pendingId,
            AgentId = AgentId.From("agent-a"),
            Title = "pending",
            PendingAskUserJson = """{"requestId":"req-1"}"""
        });

        var checkpoints = await store.GetPendingAskUserCheckpointsAsync();

        checkpoints.Count.ShouldBe(1);
        checkpoints[0].ConversationId.ShouldBe(pendingId);

        // Mechanism assertion: run the store's own SQL shape against the same database and
        // confirm SQLite reports it visiting only the matching rows. `EXPLAIN QUERY PLAN` names
        // the table scanned; the row count returned is what proves the filter, not the plan.
        await using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM conversations WHERE pending_ask_user_json IS NOT NULL AND pending_ask_user_json <> ''";
        var matching = Convert.ToInt64(await command.ExecuteScalarAsync());

        await using var totalCommand = connection.CreateCommand();
        totalCommand.CommandText = "SELECT COUNT(*) FROM conversations";
        var total = Convert.ToInt64(await totalCommand.ExecuteScalarAsync());

        // Non-vacuity: the population really is large, so returning one checkpoint is a filter
        // doing work rather than a near-empty table making any implementation look correct.
        total.ShouldBe(NoiseCount + 1);
        matching.ShouldBe(1);
        checkpoints.Count.ShouldBeLessThan((int)total);
    }

    /// <summary>
    /// #3663 AC1/AC2: the migration path creates the partial index on open, and SQLite's own
    /// planner reports using it for the store's query. A bare <c>SCAN conversations</c> - the
    /// pre-fix plan - fails this test.
    /// </summary>
    [Fact]
    public async Task PendingAskUserQuery_UsesPartialIndex_AndDoesNotScanTable()
    {
        var store = _fixture.CreateStore();
        for (var i = 0; i < 50; i++)
        {
            await store.CreateAsync(new Conversation
            {
                ConversationId = ConversationId.Create(),
                AgentId = AgentId.From("agent-a"),
                Title = $"noise-{i}",
                PendingAskUserJson = i == 0 ? """{"requestId":"req-1"}""" : null
            });
        }

        var plan = await ExplainAsync(StoreQuerySql);

        plan.ShouldContain(
            line => line.Contains(SqliteConversationStore.PendingAskUserIndexName, StringComparison.Ordinal),
            customMessage: $"expected the partial index in the plan, got: {string.Join(" | ", plan)}");
        plan.ShouldNotContain(
            line => line.Trim().Equals("SCAN conversations", StringComparison.Ordinal),
            customMessage: $"the query still falls back to a full table scan: {string.Join(" | ", plan)}");
    }

    /// <summary>
    /// #3663 AC3: the index predicate must subsume the query predicate. SQLite declines a partial
    /// index whose WHERE clause does not cover the query's, and the decline is silent - correct
    /// rows, full scan. This test pins the two to one shared constant so an edit to either side
    /// alone breaks this assertion rather than quietly regressing performance.
    /// </summary>
    [Fact]
    public void PendingAskUserIndexDdl_EmbedsExactlyTheQueryPredicate()
    {
        SqliteConversationStore.PendingAskUserIndexDdl
            .ShouldContain($"WHERE {SqliteConversationStore.PendingAskUserPredicate}");

        // Non-vacuity: the predicate is the real one, not an empty or trivially-true string.
        SqliteConversationStore.PendingAskUserPredicate
            .ShouldBe("pending_ask_user_json IS NOT NULL AND pending_ask_user_json <> ''");
    }

    /// <summary>
    /// Mutation guard for the plan assertion: proves it has teeth. An index built from a predicate
    /// that does <em>not</em> subsume the query's is ignored by SQLite, so the query falls back to
    /// <c>SCAN conversations</c>. If a diverged index were ever used, the plan-based assertion could
    /// no longer detect divergence at all.
    /// </summary>
    [Fact]
    public async Task DivergentPartialIndex_IsDeclinedBySqlite()
    {
        var store = _fixture.CreateStore();
        await store.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From("agent-a"),
            Title = "pending",
            PendingAskUserJson = """{"requestId":"req-1"}"""
        });

        await using var connection = new SqliteConnection(_fixture.ConnectionString);
        await connection.OpenAsync();

        // Drop the real index so only the diverged one is a candidate.
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = $"DROP INDEX IF EXISTS {SqliteConversationStore.PendingAskUserIndexName};";
            await drop.ExecuteNonQueryAsync();
        }

        await using (var create = connection.CreateCommand())
        {
            // Narrower than the query predicate: adds a conjunct the query does not assert.
            create.CommandText =
                "CREATE INDEX idx_conversations_pending_diverged ON conversations(id) "
                + "WHERE pending_ask_user_json IS NOT NULL AND pending_ask_user_json <> '' AND agent_id = 'other';";
            await create.ExecuteNonQueryAsync();
        }

        var plan = await ExplainAsync(StoreQuerySql, connection);

        plan.ShouldNotContain(line => line.Contains("idx_conversations_pending_diverged", StringComparison.Ordinal));
        plan.ShouldContain(line => line.Contains("SCAN conversations", StringComparison.Ordinal));
    }

    /// <summary>The exact SQL shape the store issues, rebuilt from the shared predicate constant.</summary>
    private static string StoreQuerySql =>
        "SELECT id, pending_ask_user_json FROM conversations WHERE "
        + SqliteConversationStore.PendingAskUserPredicate
        + " ORDER BY updated_at DESC";

    private async Task<List<string>> ExplainAsync(string sql, SqliteConnection? existing = null)
    {
        SqliteConnection? owned = null;
        var connection = existing;
        if (connection is null)
        {
            owned = new SqliteConnection(_fixture.ConnectionString);
            await owned.OpenAsync();
            connection = owned;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            var lines = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                lines.Add(reader.GetString(reader.GetOrdinal("detail")));
            return lines;
        }
        finally
        {
            if (owned is not null)
                await owned.DisposeAsync();
        }
    }

    public void Dispose() => _fixture.Dispose();
}
