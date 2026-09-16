using Microsoft.Data.Sqlite;

namespace BotNexus.Persistence.Sqlite;

/// <summary>
/// Executes an additive SQLite schema migration while treating only SQLite's
/// duplicate-column error as an idempotent no-op.
/// </summary>
public static class SqliteAdditiveMigration
{
    public static async Task ExecuteAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (IsDuplicateColumn(exception))
        {
            // The requested column already has the schema this migration would add.
        }
    }

    public static bool IsDuplicateColumn(SqliteException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception.SqliteErrorCode == 1
            && exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase);
    }
}
