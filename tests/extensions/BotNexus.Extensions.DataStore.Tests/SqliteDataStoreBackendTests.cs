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

    // ── Update ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ModifiesMatchingRows()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("tasks", """[{"id":1,"status":"open"},{"id":2,"status":"open"}]""");

        var result = await backend.UpdateAsync("tasks", """{"status":"done"}""", "id = 1");

        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(1);

        var query = await backend.QueryAsync("SELECT status FROM tasks WHERE id = 1");
        using var doc = JsonDocument.Parse(query.Payload ?? "[]");
        doc.RootElement[0].GetProperty("status").GetString().ShouldBe("done");
    }

    [Fact]
    public async Task Update_MultipleColumns_AllUpdated()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("items", """[{"id":1,"name":"old","count":0}]""");

        var result = await backend.UpdateAsync("items", """{"name":"new","count":5}""", "id = 1");

        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(1);

        var query = await backend.QueryAsync("SELECT name, count FROM items WHERE id = 1");
        using var doc = JsonDocument.Parse(query.Payload ?? "[]");
        doc.RootElement[0].GetProperty("name").GetString().ShouldBe("new");
        doc.RootElement[0].GetProperty("count").GetInt64().ShouldBe(5);
    }

    [Fact]
    public async Task Update_NoMatchingRows_Returns0()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("tasks", """[{"id":1,"status":"open"}]""");

        var result = await backend.UpdateAsync("tasks", """{"status":"done"}""", "id = 999");

        result.Success.ShouldBeTrue(result.Error);
        result.RowCount.ShouldBe(0);
    }

    [Fact]
    public async Task Update_InvalidJson_ReturnsError()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("tasks", """[{"id":1,"status":"open"}]""");

        var result = await backend.UpdateAsync("tasks", "not-json", "id = 1");

        result.Success.ShouldBeFalse();
        (result.Error ?? string.Empty).ShouldContain("Update failed");
    }

    [Fact]
    public async Task Update_EmptySetObject_ReturnsError()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("tasks", """[{"id":1,"status":"open"}]""");

        var result = await backend.UpdateAsync("tasks", "{}", "id = 1");

        result.Success.ShouldBeFalse();
        (result.Error ?? string.Empty).ShouldContain("at least one");
    }

    [Fact]
    public async Task Update_NonObjectJson_ReturnsError()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("tasks", """[{"id":1,"status":"open"}]""");

        var result = await backend.UpdateAsync("tasks", "[1,2,3]", "id = 1");

        result.Success.ShouldBeFalse();
        (result.Error ?? string.Empty).ShouldContain("JSON object");
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

    // ── Count ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Count_FullTable_ReturnsCorrectCount()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("items", """[{"id":1},{"id":2},{"id":3}]""");
        var result = await backend.CountAsync("items");
        result.Success.ShouldBeTrue();
        result.RowCount.ShouldBe(3);
        result.Payload!.ShouldContain("3");
    }

    [Fact]
    public async Task Count_WithWhere_ReturnsFilteredCount()
    {
        var backend = CreateBackend();
        await backend.IngestAsync("items", """[{"id":1,"status":"done"},{"id":2,"status":"open"},{"id":3,"status":"done"}]""");
        var result = await backend.CountAsync("items", "status = 'done'");
        result.Success.ShouldBeTrue();
        result.RowCount.ShouldBe(2);
    }

    [Fact]
    public async Task Count_NonExistentTable_ReturnsError()
    {
        var backend = CreateBackend();
        var result = await backend.CountAsync("nonexistent");
        result.Success.ShouldBeFalse();
        (result.Error ?? string.Empty).ShouldContain("Count failed");
    }

    // ── Query row limit ───────────────────────────────────────────────────────

    private SqliteDataStoreBackend CreateBackendWithQueryLimit(int maxQueryRows)
    {
        _backend?.Dispose();
        var dbPath = Path.Combine(_dbDir, ".store", "agent-data.db");
        _backend = new SqliteDataStoreBackend(dbPath, 50 * 1024 * 1024, maxQueryRows);
        return _backend;
    }

    [Fact]
    public async Task Query_UnderLimit_ReturnsAllRows()
    {
        var backend = CreateBackendWithQueryLimit(10);
        var rows = Enumerable.Range(1, 5).Select(i => new { id = i, name = $"user{i}" });
        await backend.IngestAsync("items", System.Text.Json.JsonSerializer.Serialize(rows));

        var result = await backend.QueryAsync("SELECT * FROM items");

        result.Success.ShouldBeTrue();
        result.RowCount.ShouldBe(5);
        result.Payload!.ShouldNotContain("truncated");
    }

    [Fact]
    public async Task Query_ExceedsLimit_TruncatesWithWarning()
    {
        var backend = CreateBackendWithQueryLimit(3);
        var rows = Enumerable.Range(1, 10).Select(i => new { id = i, name = $"user{i}" });
        await backend.IngestAsync("items", System.Text.Json.JsonSerializer.Serialize(rows));

        var result = await backend.QueryAsync("SELECT * FROM items");

        result.Success.ShouldBeTrue();
        result.RowCount.ShouldBe(3);
        result.Payload!.ShouldContain("truncated to 3 rows");
    }

    [Fact]
    public async Task Query_ExactlyAtLimit_DoesNotTruncate()
    {
        var backend = CreateBackendWithQueryLimit(5);
        var rows = Enumerable.Range(1, 5).Select(i => new { id = i, name = $"user{i}" });
        await backend.IngestAsync("items", System.Text.Json.JsonSerializer.Serialize(rows));

        var result = await backend.QueryAsync("SELECT * FROM items");

        result.Success.ShouldBeTrue();
        result.RowCount.ShouldBe(5);
        result.Payload!.ShouldNotContain("truncated");
    }

    [Fact]
    public async Task Query_WithExplicitLimit_RespectsUserLimit()
    {
        var backend = CreateBackendWithQueryLimit(3);
        var rows = Enumerable.Range(1, 10).Select(i => new { id = i, name = $"user{i}" });
        await backend.IngestAsync("items", System.Text.Json.JsonSerializer.Serialize(rows));

        var result = await backend.QueryAsync("SELECT * FROM items LIMIT 2");

        result.Success.ShouldBeTrue();
        result.RowCount.ShouldBe(2);
        result.Payload!.ShouldNotContain("truncated");
    }
}
