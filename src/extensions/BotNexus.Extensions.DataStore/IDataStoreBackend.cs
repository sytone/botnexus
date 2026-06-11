namespace BotNexus.Extensions.DataStore;

/// <summary>
/// Pluggable backend for the data store tool.
/// The default implementation (provided by <c>BotNexus.Extensions.DataStore</c>) uses SQLite.
/// Inject a test double in unit tests.
/// </summary>
public interface IDataStoreBackend : IDisposable
{
    /// <summary>
    /// Ingest a JSON array of objects, inferring or updating the table schema.
    /// Non-destructive: new fields are added via ALTER TABLE ADD COLUMN.
    /// </summary>
    Task<DataStoreResult> IngestAsync(string table, string json, CancellationToken ct = default);

    /// <summary>Execute a read-only SELECT query.</summary>
    Task<DataStoreResult> QueryAsync(string sql, CancellationToken ct = default);

    /// <summary>Insert a single JSON object row into the given table.</summary>
    Task<DataStoreResult> InsertAsync(string table, string json, CancellationToken ct = default);

    /// <summary>Delete rows matching a WHERE clause.</summary>
    Task<DataStoreResult> DeleteAsync(string table, string where, CancellationToken ct = default);

    /// <summary>Update rows matching a WHERE clause with the provided column values.</summary>
    Task<DataStoreResult> UpdateAsync(string table, string set, string where, CancellationToken ct = default);

    /// <summary>Return the schema (column names and types) for a table.</summary>
    Task<DataStoreResult> SchemaAsync(string table, CancellationToken ct = default);

    /// <summary>List all tables in the data store.</summary>
    Task<DataStoreResult> TablesAsync(CancellationToken ct = default);

    /// <summary>Count rows in a table, optionally filtered by a WHERE clause.</summary>
    Task<DataStoreResult> CountAsync(string table, string? where = null, CancellationToken ct = default);

    /// <summary>Drop a table entirely.</summary>
    Task<DataStoreResult> DropAsync(string table, CancellationToken ct = default);
}

/// <summary>
/// Result from a <see cref="IDataStoreBackend"/> operation.
/// </summary>
public sealed class DataStoreResult
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Human-readable result payload (JSON rows, schema lines, table list, etc.).</summary>
    public string? Payload { get; init; }

    /// <summary>Error message when <see cref="Success"/> is false.</summary>
    public string? Error { get; init; }

    /// <summary>Number of rows affected or returned, if applicable.</summary>
    public int? RowCount { get; init; }

    public static DataStoreResult Ok(string payload, int? rowCount = null) =>
        new() { Success = true, Payload = payload, RowCount = rowCount };

    public static DataStoreResult Fail(string error) =>
        new() { Success = false, Error = error };
}
