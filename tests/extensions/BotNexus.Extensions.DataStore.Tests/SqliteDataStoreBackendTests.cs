using BotNexus.Extensions.DataStore;
using Shouldly;
using System.Text.Json;

namespace BotNexus.Extensions.DataStore.Tests;

/// <summary>
/// Integration tests for <see cref="SqliteDataStoreBackend"/> using a temp SQLite file.
/// </summary>
public sealed class SqliteDataStoreBackendTests : IDisposable
{
    private readonly string _dbDir = Path.Combine(Path.GetTempPath(), $"ds-test-{Guid.NewGuid():N}");
    private SqliteDataStoreBackend? _backend;

    public void Dispose()
    {
        _backend?.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dbDir)) Directory.Delete(_dbDir, recursive: true);
    }

    private SqliteDataStoreBackend CreateBackend(long maxBytes = 50 * 1024 * 1024)
    {
        _backend?.Dispose();
        var dbPath = Path.Combine(_dbDir, ".store", "agent-data.db");
        _backend = new SqliteDataStoreBackend(dbPath, maxBytes);
        return _backend;
    }

    // ── Ingest creates table ──────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_CreatesTableAndReturnsRowCount()
    {
        var backend = CreateBackend();
        var json = """[{"id":1,"name":"Alice"},{"id":2,"name":"Bob"}]""";

        var result = await backend.IngestAsync("users", json);

        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(2);
        (result.Payload ?? string.Empty).ShouldContain("2 rows");
    }

    [Fact]
    public async Task Ingest_SingleRow_ReturnsRowCount1()
    {
        var backend = CreateBackend();
        var json = """[{"id":1}]""";

        var result = await backend.IngestAsync("items", json);

        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(1);
    }

    [Fact]
    public async Task Ingest_EmptyArray_Returns0Rows()
    {
        var backend = CreateBackend();
        var result = await backend.IngestAsync("items", "[]");
        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(0);
    }

    [Fact]
    public async Task Ingest_NonArrayJson_ReturnsError()
    {
        var backend = CreateBackend();
        var result = await backend.IngestAsync("items", """{"id":1}""");
        result.Success.ShouldBeFalse();
        (result.Error ?? string.Empty).ShouldContain("array");
    }

    // ── Query returns results ────────────────────────────────────────────────

    [Fact]
    public async Task Query_AfterIngest_ReturnsRows()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("events", """[{"id":1,"type":"login"},{"id":2,"type":"logout"}]""");

        var result = await backend.QueryAsync("SELECT * FROM events ORDER BY id");

        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(2);

        using var doc = JsonDocument.Parse(result.Payload ?? "[]");
        doc.RootElement.GetArrayLength().ShouldBe(2);
        doc.RootElement[0].GetProperty("type").GetString().ShouldBe("login");
    }

    [Fact]
    public async Task Query_SelectWithWhere_FiltersRows()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("events", """[{"id":1,"type":"login"},{"id":2,"type":"logout"}]""");

        var result = await backend.QueryAsync("SELECT * FROM events WHERE type = 'login'");

        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(1);
    }

    // ── Schema inference ─────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_InfersIntegerColumnForNumbers()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("nums", """[{"count":42}]""");

        var schema = await backend.SchemaAsync("nums");

        schema.Success.ShouldBeTrue(schema.Error);
        var payload = schema.Payload ?? string.Empty;
        payload.ShouldContain("count");
        payload.ShouldContain("INTEGER");
    }

    [Fact]
    public async Task Ingest_InfersRealColumnForFloats()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("metrics", """[{"value":3.14}]""");

        var schema = await backend.SchemaAsync("metrics");

        schema.Success.ShouldBeTrue(schema.Error);
        var payload = schema.Payload ?? string.Empty;
        payload.ShouldContain("value");
        payload.ShouldContain("REAL");
    }

    [Fact]
    public async Task Ingest_InfersTextColumnForStrings()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("labels", """[{"name":"hello"}]""");

        var schema = await backend.SchemaAsync("labels");

        schema.Success.ShouldBeTrue(schema.Error);
        var payload = schema.Payload ?? string.Empty;
        payload.ShouldContain("name");
        payload.ShouldContain("TEXT");
    }

    [Fact]
    public async Task Ingest_NonDestructiveAlterTable_AddsMissingColumns()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("docs", """[{"id":1}]""");
        await backend.IngestAsync("docs", """[{"id":2,"title":"Hello"}]""");

        var schema = await backend.SchemaAsync("docs");

        schema.Success.ShouldBeTrue(schema.Error);
        var payload = schema.Payload ?? string.Empty;
        payload.ShouldContain("id");
        payload.ShouldContain("title");
    }

    // ── Tables and Drop ──────────────────────────────────────────────────────

    [Fact]
    public async Task Tables_ReturnsTableNames()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("alpha", """[{"x":1}]""");
        await backend.IngestAsync("beta", """[{"y":2}]""");

        var result = await backend.TablesAsync();

        result.Success.ShouldBeTrue(result.Error);
        var payload = result.Payload ?? string.Empty;
        payload.ShouldContain("alpha");
        payload.ShouldContain("beta");
    }

    [Fact]
    public async Task Tables_EmptyDb_ReturnsNoTables()
    {
        var backend = CreateBackend();
        var result = await backend.TablesAsync();
        result.Success.ShouldBeTrue();
        (result.Payload ?? string.Empty).ShouldContain("no tables");
    }

    [Fact]
    public async Task Drop_RemovesTable()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("temp_table", """[{"x":1}]""");
        await backend.DropAsync("temp_table");

        var tables = await backend.TablesAsync();
        (tables.Payload ?? string.Empty).ShouldNotContain("temp_table");
    }

    // ── Size limit ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Ingest_WhenExceedsSizeLimit_ReturnsError()
    {
        // Use a very small size limit (1 byte) to force failure after any write
        var backend = CreateBackend(maxBytes: 1);
        var json = """[{"id":1,"name":"Alice"}]""";

        var result = await backend.IngestAsync("users", json);

        result.Success.ShouldBeFalse();
        (result.Error ?? string.Empty).ShouldContain("exceeds limit");
    }

    // ── Insert ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Insert_AddsRowToExistingTable()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("events", """[{"id":1,"type":"a"}]""");

        var result = await backend.InsertAsync("events", """{"id":2,"type":"b"}""");

        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(1);

        var query = await backend.QueryAsync("SELECT count(*) as cnt FROM events");
        using var doc = JsonDocument.Parse(query.Payload ?? "[]");
        doc.RootElement[0].GetProperty("cnt").GetInt64().ShouldBe(2);
    }

    [Fact]
    public async Task Insert_NonExistentTable_ReturnsError()
    {
        var backend = CreateBackend();
        var result = await backend.InsertAsync("nonexistent", """{"x":1}""");
        result.Success.ShouldBeFalse();
        (result.Error ?? string.Empty).ShouldContain("does not exist");
    }

    [Fact]
    public async Task Insert_NonObjectJson_ReturnsError()
    {
        var backend = CreateBackend();
        var result = await backend.InsertAsync("events", """[{"id":1}]""");
        result.Success.ShouldBeFalse();
        (result.Error ?? string.Empty).ShouldContain("object");
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesMatchingRows()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("logs", """[{"id":1,"active":1},{"id":2,"active":0}]""");

        var result = await backend.DeleteAsync("logs", "active = 0");

        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(1);

        var query = await backend.QueryAsync("SELECT count(*) as cnt FROM logs");
        using var doc = JsonDocument.Parse(query.Payload ?? "[]");
        doc.RootElement[0].GetProperty("cnt").GetInt64().ShouldBe(1);
    }

    // ── Schema not found ─────────────────────────────────────────────────────

    [Fact]
    public async Task Schema_NonExistentTable_ReturnsError()
    {
        var backend = CreateBackend();
        var result = await backend.SchemaAsync("nonexistent");
        result.Success.ShouldBeFalse();
        (result.Error ?? string.Empty).ShouldContain("does not exist");
    }
}
