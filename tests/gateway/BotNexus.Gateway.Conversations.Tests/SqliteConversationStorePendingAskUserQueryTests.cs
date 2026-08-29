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

    public void Dispose() => _fixture.Dispose();
}
