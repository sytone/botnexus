using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using Microsoft.Data.Sqlite;
namespace BotNexus.Gateway.Conversations.Tests;
public sealed class SqliteConversationStoreActivityQueryTests
{
    [Fact]
    public async Task Cutoff_filters_old_rows_in_sql_before_summary_mapping()
    {
        using var fixture = new StoreFixture();
        var store = fixture.CreateStore();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        foreach (var (id, at) in new[] { ("old", cutoff.AddTicks(-1)), ("boundary", cutoff) })
            await store.CreateAsync(new Conversation
            {
                ConversationId = ConversationId.From(id), AgentId = AgentId.From("alpha"), Title = id,
                CreatedAt = cutoff.AddDays(-2), UpdatedAt = at
            });
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var columnsCommand = connection.CreateCommand();
        columnsCommand.CommandText = "SELECT * FROM conversations LIMIT 0";
        var columns = new List<string>();
        await using (var reader = await columnsCommand.ExecuteReaderAsync())
            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(reader.GetName(i));
        await using var poison = connection.CreateCommand();
        // A computed title fails only if SQL projects the excluded old row. Filtering after
        // response mapping cannot pass this guard. This view changes only the test database.
        var projection = string.Join(", ", columns.Select(column => column == "title"
            ? "CASE WHEN id = 'old' THEN json('invalid json') ELSE title END AS title"
            : column));
        poison.CommandText = $"ALTER TABLE conversations RENAME TO stored_conversations; CREATE VIEW conversations AS SELECT {projection} FROM stored_conversations";
        await poison.ExecuteNonQueryAsync();
        var bounded = await store.GetSummariesAsync(cutoff);
        bounded.ShouldHaveSingleItem().ConversationId.ShouldBe("boundary");
        await Should.ThrowAsync<SqliteException>(async () => await store.GetSummariesAsync());
    }
}
