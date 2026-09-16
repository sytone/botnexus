using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite.Tests;

public sealed class SqliteAdditiveMigrationTests
{
    [Fact]
    public async Task ExecuteAsync_DuplicateColumn_IsSilentAndIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE sample (id INTEGER PRIMARY KEY, value TEXT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        await using var migration = connection.CreateCommand();
        migration.CommandText = "ALTER TABLE sample ADD COLUMN value TEXT NULL;";

        await SqliteAdditiveMigration.ExecuteAsync(migration, CancellationToken.None);
        await SqliteAdditiveMigration.ExecuteAsync(migration, CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_NonDuplicateSqliteFailure_PropagatesWithNativeCode()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var migration = connection.CreateCommand();
        migration.CommandText = "ALTER TABLE missing_table ADD COLUMN value TEXT NULL;";

        var exception = await Should.ThrowAsync<SqliteException>(
            () => SqliteAdditiveMigration.ExecuteAsync(migration, CancellationToken.None));

        exception.SqliteErrorCode.ShouldBe(1);
        exception.Message.ShouldContain("no such table");
    }
}
