using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Data.Sqlite;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class ConfigParityHardeningTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"parity-hardening-{Guid.NewGuid():N}");
    private string ConfigPath => Path.Combine(_home, "config.json");
    private string StorePath => Path.Combine(_home, ConfigStoreBootstrap.StoreFileName);
    public ConfigParityHardeningTests() => Directory.CreateDirectory(_home);

    private async Task SeedAsync(string compatibility = "{}")
    {
        await ConfigStoreBootstrap.PopulateAsync(StorePath, new JsonObject());
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        await File.WriteAllTextAsync(ConfigPath, compatibility);
    }

    private async Task ExecuteAsync(string commandText)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = StorePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    // Read-only means no application-state mutation. WAL/SHM are SQLite coordination files,
    // not configuration state; comparing their bytes/file set was the wrong former contract.
    private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(_home)
        .Where(path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
        .ToDictionary(path => path, File.ReadAllBytes);
    private void AssertUnchanged(Dictionary<string, byte[]> before)
    {
        Snapshot().Keys.Order().ShouldBe(before.Keys.Order());
        foreach (var pair in before)
            File.ReadAllBytes(pair.Key).ShouldBe(pair.Value);
        Directory.GetDirectories(_home).ShouldBeEmpty();
    }

    private async Task<string> ReadApplicationStateAsync(SqliteConnection connection)
    {
        var state = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT scope,scope_id,key_path,state,value FROM config_entries ORDER BY scope,scope_id,key_path;",
            "SELECT singleton,revision FROM config_revision ORDER BY singleton;",
            "SELECT key,value FROM store_meta ORDER BY key;",
            "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name;",
            "PRAGMA user_version;"
        })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                state.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
        }
        return string.Join("\n", state);
    }

    [Fact]
    public async Task InspectAsync_ProductionDefaultWalWithoutSidecars_SucceedsWithoutApplicationMutation()
    {
        await SeedAsync();
        File.Exists(StorePath + "-wal").ShouldBeFalse();
        File.Exists(StorePath + "-shm").ShouldBeFalse();
        File.ReadAllBytes(StorePath)[18].ShouldBe((byte)2); // Persisted WAL header, not rollback fixture.
        var before = Snapshot();
        var report = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        report.Status.ShouldBe(ConfigStoreParityStatus.Clean);
        report.SqlBackend.ShouldBe(ConfigParityBackendState.Readable);
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeTrue();
        AssertUnchanged(before);
        await using var observer = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = StorePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        await observer.OpenAsync();
        var applicationBefore = await ReadApplicationStateAsync(observer);
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).Status.ShouldBe(ConfigStoreParityStatus.Clean);
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeTrue();
        (await ReadApplicationStateAsync(observer)).ShouldBe(applicationBefore);
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData(2, ConfigStoreParityStatus.Clean)]
    [InlineData(1, ConfigStoreParityStatus.Drift)]
    public async Task InspectAsync_CommittedWalWhileWriterActive_ReadsWalNotStaleMain(int compatibilityValue, ConfigStoreParityStatus expected)
    {
        await SeedAsync($"{{\"x\":{compatibilityValue}}}");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = StorePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO config_entries VALUES (0,'','x',3,'1'); PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync();
        var mainBeforeWalCommit = File.ReadAllBytes(StorePath);
        command.CommandText = "PRAGMA journal_mode;";
        (await command.ExecuteScalarAsync()).ShouldBe("wal");
        command.CommandText = "PRAGMA wal_autocheckpoint=0; UPDATE config_entries SET value='2' WHERE key_path='x'; UPDATE config_revision SET revision=revision+1;";
        await command.ExecuteNonQueryAsync();
        new FileInfo(StorePath + "-wal").Length.ShouldBeGreaterThan(0);
        File.ReadAllBytes(StorePath).ShouldBe(mainBeforeWalCommit); // Main still holds x=1.
        command.CommandText = "SELECT value FROM config_entries WHERE key_path='x';";
        (await command.ExecuteScalarAsync()).ShouldBe("2");
        var before = Snapshot();
        var applicationBefore = await ReadApplicationStateAsync(connection);
        var report = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        report.Status.ShouldBe(expected);
        report.SqlBackend.ShouldBe(ConfigParityBackendState.Readable);
        (await PlatformConfigWriter.PreviewReconciliationAsync(ConfigPath)).CanApply.ShouldBeTrue();
        (await ReadApplicationStateAsync(connection)).ShouldBe(applicationBefore);
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData("{\"x\":{\"private-key\":1,\"private-key\":2}}")]
    [InlineData("{\"unmatched\":[{\"private-key\":1,\"private-key\":2}]}")]
    public async Task InspectAsync_NestedDuplicateCompatibility_AttributesUnreadableWithoutPayload(string json)
    {
        await SeedAsync(json);
        var before = Snapshot();
        var report = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        report.CompatibilityBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        JsonSerializer.Serialize(report).ShouldNotContain("private-key");
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData("PRAGMA user_version=999;")]
    [InlineData("UPDATE config_entries SET state = 'not-an-integer'; INSERT INTO config_entries VALUES (0,'','bad', 'not-an-integer', NULL);")]
    [InlineData("DELETE FROM store_meta WHERE key='schema_version';")]
    [InlineData("INSERT INTO config_entries VALUES (0,'','x',3,'{\"unmatched\":{\"private-key\":1,\"private-key\":2}}');")]
    [InlineData("UPDATE store_meta SET value='999' WHERE key='schema_version';")]
    [InlineData("INSERT INTO store_meta VALUES ('store_kind','sessions');")]
    [InlineData("INSERT INTO store_meta VALUES ('world_id','foreign-world');")]
    [InlineData("INSERT INTO config_entries VALUES (0,'','x',0,NULL); INSERT INTO config_entries VALUES (0,'','x.child',3,'1');")]
    [InlineData("INSERT INTO config_entries VALUES (0,'','x',1,NULL); INSERT INTO config_entries VALUES (0,'','x.child',3,'1');")]
    [InlineData("INSERT INTO config_entries VALUES (0,'','x',3,'[{\"private-key\":1,\"private-key\":2}]');")]
    public async Task InspectAsync_UnsupportedSql_RefusesWithoutRepair(string sql)
    {
        await SeedAsync();
        await ExecuteAsync(sql);
        var before = Snapshot();
        var report = await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath);
        report.SqlBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        JsonSerializer.Serialize(report).ShouldNotContain("private-key");
        AssertUnchanged(before);
    }

    [Fact]
    public async Task InspectAsync_CompatibilityByteLimit_RefusesCompleteInputNotTruncation()
    {
        await SeedAsync("{\"payload\":\"" + new string('x', ConfigStoreParityDiagnostic.MaximumDocumentBytes) + "\"}");
        var before = Snapshot();
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).CompatibilityBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        AssertUnchanged(before);
    }

    [Fact]
    public async Task InspectAsync_SqlRowLimit_RefusesNotTruncatedClean()
    {
        await SeedAsync();
        await ExecuteAsync($"WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x < {ConfigStoreParityDiagnostic.MaximumSqlRows + 1}) INSERT INTO config_entries SELECT 0,'','key'||x,3,'1' FROM n;");
        var before = Snapshot();
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).SqlBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        AssertUnchanged(before);
    }

    [Fact]
    public async Task InspectAsync_DefaultWalStore_ReadsCompleteDocumentWithoutMutation()
    {
        await SeedAsync("{\"x\":1}");
        await ExecuteAsync("INSERT INTO config_entries VALUES (0,'','x',3,'1');");
        var before = Snapshot();
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).Status.ShouldBe(ConfigStoreParityStatus.Clean);
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InspectAsync_CompatibilityComplexityLimits_RefusesWithoutMutation(int scenario)
    {
        var payload = scenario switch
        {
            0 => new string('[', 40) + "0" + new string(']', 40),
            1 => "{\"" + new string('k', 1025) + "\":1}",
            _ => "{\"nodes\":[" + string.Join(',', Enumerable.Repeat("0", 100_001)) + "]}"
        };
        await SeedAsync(payload);
        var before = Snapshot();
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).CompatibilityBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        AssertUnchanged(before);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InspectAsync_SqlComplexityLimits_RefusesWithoutMutation(int scenario)
    {
        await SeedAsync();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = StorePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO config_entries VALUES (0,'',$path,3,$value);";
            command.Parameters.AddWithValue("$path", scenario == 0 ? new string('k', 1025) : "x");
            command.Parameters.AddWithValue("$value", scenario == 1
                ? new string('[', 40) + "0" + new string(']', 40)
                : scenario == 2 ? "\"" + new string('x', ConfigStoreParityDiagnostic.MaximumDocumentBytes) + "\"" : "1");
            await command.ExecuteNonQueryAsync();
        }
        var before = Snapshot();
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).SqlBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        AssertUnchanged(before);
    }

    [Fact]
    public async Task InspectAsync_SqlAggregateLimit_RefusesWithoutMutation()
    {
        await SeedAsync();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = StorePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO config_entries VALUES (0,'','first',3,$value); INSERT INTO config_entries VALUES (0,'','second',3,$value);";
            command.Parameters.AddWithValue("$value", "\"" + new string('x', ConfigStoreParityDiagnostic.MaximumDocumentBytes / 2) + "\"");
            await command.ExecuteNonQueryAsync();
        }
        var before = Snapshot();
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).SqlBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        AssertUnchanged(before);
    }

    [Fact]
    public async Task InspectAsync_HotRollbackJournal_RefusesWithoutRecovery()
    {
        await SeedAsync();
        await File.WriteAllTextAsync(StorePath + "-journal", "private-recovery-state");
        var before = Snapshot();
        (await ConfigStoreParityDiagnostic.InspectAsync(ConfigPath)).SqlBackend.ShouldBe(ConfigParityBackendState.Unreadable);
        AssertUnchanged(before);
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_home, true);
    }
}
