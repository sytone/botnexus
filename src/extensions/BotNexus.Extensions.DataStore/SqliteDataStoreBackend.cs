namespace BotNexus.Extensions.DataStore;

/// <summary>
/// SQLite-backed implementation of <see cref="IDataStoreBackend"/>.
/// Implementation provided in issue #853 (SQLite storage backend).
/// This stub satisfies DI requirements and allows <see cref="DataStoreTool"/>
/// to be tested independently of the SQLite layer.
/// </summary>
internal sealed class SqliteDataStoreBackend(string dbPath, long maxSizeBytes) : IDataStoreBackend
{
    private readonly string _dbPath       = dbPath;
    private readonly long   _maxSizeBytes = maxSizeBytes;

    public Task<DataStoreResult> IngestAsync(string table, string json, CancellationToken ct = default) =>
        Task.FromResult(DataStoreResult.Fail("SQLite backend not yet implemented (see #853)."));

    public Task<DataStoreResult> QueryAsync(string sql, CancellationToken ct = default) =>
        Task.FromResult(DataStoreResult.Fail("SQLite backend not yet implemented (see #853)."));

    public Task<DataStoreResult> InsertAsync(string table, string json, CancellationToken ct = default) =>
        Task.FromResult(DataStoreResult.Fail("SQLite backend not yet implemented (see #853)."));

    public Task<DataStoreResult> DeleteAsync(string table, string where, CancellationToken ct = default) =>
        Task.FromResult(DataStoreResult.Fail("SQLite backend not yet implemented (see #853)."));

    public Task<DataStoreResult> SchemaAsync(string table, CancellationToken ct = default) =>
        Task.FromResult(DataStoreResult.Fail("SQLite backend not yet implemented (see #853)."));

    public Task<DataStoreResult> TablesAsync(CancellationToken ct = default) =>
        Task.FromResult(DataStoreResult.Fail("SQLite backend not yet implemented (see #853)."));

    public Task<DataStoreResult> DropAsync(string table, CancellationToken ct = default) =>
        Task.FromResult(DataStoreResult.Fail("SQLite backend not yet implemented (see #853)."));

    public void Dispose() { /* no-op until SQLite connection added in #853 */ }
}
