using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Shadow;
using BotNexus.Gateway.Configuration.Store;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Round-trip tests for the SQLite configuration store (#2646 PBI 1).
///
/// <para>
/// <b>What is actually being proven.</b> Not that SQLite can store strings - that the store preserves
/// the distinctions the JSON document carries, in particular the tri-state that a nullable column
/// destroys by default. Every test here runs against a real on-disk database rather than a fake,
/// because the failure mode being guarded against (a column mapping both "unset" and "explicitly null"
/// onto <c>NULL</c>) lives in the storage layer itself and a fake would reproduce whatever the author
/// already believed.
/// </para>
/// </summary>
public sealed class SqliteConfigStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"botnexus-config-store-{Guid.NewGuid():N}.db");

    private SqliteConfigStore CreateStore() => new($"Data Source={_dbPath}");

    private static JsonObject Obj(string raw) => JsonNode.Parse(raw)!.AsObject();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { /* best effort in temp */ }
            }
        }
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsScalarValues()
    {
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""{ "gateway": { "threshold": 10, "name": "alpha" } }"""));

        var entries = await store.ReadEntriesAsync();

        entries["gateway.threshold"].State.ShouldBe(ConfigValueState.Value);
        entries["gateway.threshold"].Value.ShouldBe("10");
        entries["gateway.name"].Value.ShouldBe("\"alpha\"");
    }

    /// <summary>
    /// The load-bearing test: an explicit null survives storage as <see cref="ConfigValueState.ExplicitNull"/>,
    /// not as an absent row.
    ///
    /// <para>
    /// This is what a nullable column gets wrong by default. If the store recorded "no value" and
    /// dropped the row, every agent that had explicitly declined a world default would silently be
    /// handed it back on the next read - no exception, no log line, and the affected agents behave
    /// subtly differently forever.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExplicitNull_SurvivesRoundTrip_AsExplicitNull_NotAsAnAbsentRow()
    {
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""{ "agents": { "alpha": { "memory": null, "model": "x" } } }"""));

        var entries = await store.ReadEntriesAsync();

        entries.ShouldContainKey("agents.alpha.memory");
        entries["agents.alpha.memory"].State.ShouldBe(
            ConfigValueState.ExplicitNull,
            "an explicit null must round-trip as ExplicitNull. Storing it as an absent row would " +
            "silently restore the inherited value it exists to suppress.");
        entries["agents.alpha.memory"].Value.ShouldBeNull();
    }

    /// <summary>An absent key has no row at all - the other half of the tri-state.</summary>
    [Fact]
    public async Task AbsentKey_HasNoRow()
    {
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""{ "agents": { "alpha": { "model": "x" } } }"""));

        var entries = await store.ReadEntriesAsync();

        entries.ShouldContainKey("agents.alpha.model");
        entries.ContainsKey("agents.alpha.memory").ShouldBeFalse(
            "a key the document never mentioned must not appear as a row: absence is how 'inherit' is " +
            "expressed, and inventing a row for it would turn inheritance into suppression.");
    }

    /// <summary>
    /// The round-trip is clean under #2766's diff — the harness and the store agree.
    ///
    /// <para>
    /// Asserting through <c>ConfigShadowDiff</c> rather than by hand-comparing dictionaries is
    /// deliberate: it exercises the exact comparison shadow mode will run in production, so a store
    /// that satisfies this test satisfies the harness by construction rather than by coincidence.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RoundTrip_IsCleanUnderTheShadowDiff()
    {
        var document = Obj("""
            {
              "gateway": { "threshold": 10, "enabled": true },
              "agents": { "alpha": { "model": "x", "memory": null, "tools": ["read", "write"] } }
            }
            """);

        var store = CreateStore();
        await store.WriteDocumentAsync(document);
        var stored = await store.ReadEntriesAsync();

        var report = ConfigShadowDiff.CompareEntries(ConfigDocumentFlattener.Flatten(document), stored);

        report.IsClean.ShouldBeTrue(
            $"the store must reproduce the document exactly. Differences: {report.Summary}");
        report.SourceKeyCount.ShouldBeGreaterThan(0, "a clean diff over an empty input proves nothing");
    }

    /// <summary>Arrays are stored as single leaf values, matching how configuration merges them.</summary>
    [Fact]
    public async Task Array_IsStoredAsASingleLeaf()
    {
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""{ "tools": ["read", "write"] }"""));

        var entries = await store.ReadEntriesAsync();

        entries.ShouldContainKey("tools");
        entries["tools"].Value.ShouldBe("""["read","write"]""");
        entries.Keys.ShouldNotContain("tools[0]", "array elements are not separate configuration keys");
    }

    /// <summary>
    /// A second write replaces the previous snapshot rather than merging into it.
    ///
    /// <para>
    /// Merging would leave rows behind for keys the new document no longer has - the stale-key failure
    /// SonicJS #972 records, where a removed field persisted forever because the write path merged.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SecondWrite_ReplacesRatherThanMerges()
    {
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""{ "gateway": { "old": 1, "kept": 2 } }"""));
        await store.WriteDocumentAsync(Obj("""{ "gateway": { "kept": 2 } }"""));

        var entries = await store.ReadEntriesAsync();

        entries.ShouldContainKey("gateway.kept");
        entries.ContainsKey("gateway.old").ShouldBeFalse(
            "a removed key must not survive the next import as a stale row");
    }

    /// <summary>Opening an existing database again is safe - schema creation is idempotent.</summary>
    [Fact]
    public async Task ReopeningAnExistingDatabase_IsIdempotent()
    {
        await CreateStore().WriteDocumentAsync(Obj("""{ "a": 1 }"""));

        var entries = await CreateStore().ReadEntriesAsync();

        entries["a"].Value.ShouldBe("1");
    }

    /// <summary>Reading a store that has never been written yields no entries rather than throwing.</summary>
    [Fact]
    public async Task ReadingAnEmptyStore_ReturnsNoEntries()
    {
        var entries = await CreateStore().ReadEntriesAsync();

        entries.ShouldBeEmpty();
    }
}
