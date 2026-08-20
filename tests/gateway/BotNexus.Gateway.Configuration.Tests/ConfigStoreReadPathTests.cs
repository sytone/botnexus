using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Shadow;
using BotNexus.Gateway.Configuration.Store;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Tests for the store-backed configuration read path (#2646 PBI 3, #2766 AC7).
///
/// <para>
/// <b>What is being proven.</b> That making the store authoritative does not change what a consumer
/// receives. The cutover's failure mode is not a crash - it is a document that differs subtly from the
/// file, so agents silently behave differently. Every test here therefore compares a document against
/// the document the file would have produced, rather than asserting the store "worked".
/// </para>
/// </summary>
public sealed class ConfigStoreReadPathTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"botnexus-config-read-{Guid.NewGuid():N}.db");

    private SqliteConfigStore CreateStore() => new($"Data Source={_dbPath}");

    private static JsonObject Obj(string raw) => JsonNode.Parse(raw)!.AsObject();

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(_dbPath);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch (IOException) { /* best effort in temp */ }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Rehydration
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Rehydrate_RebuildsNestedStructure()
    {
        var source = Obj("""{ "gateway": { "compaction": { "enabled": true, "turns": 8 } } }""");

        var rebuilt = ConfigDocumentRehydrator.Rehydrate(ConfigDocumentFlattener.Flatten(source));

        rebuilt["gateway"]!["compaction"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        rebuilt["gateway"]!["compaction"]!["turns"]!.GetValue<int>().ShouldBe(8);
    }

    /// <summary>
    /// The load-bearing rehydration test: an explicit null must come back as a PRESENT key holding
    /// null, not as an absent key.
    ///
    /// <para>
    /// Absence means "inherit from the layer above" and explicit null means "suppress the inherited
    /// value". If rehydration dropped the key, every agent that had deliberately declined a world
    /// default would silently start receiving it again - no exception, no log line, permanently
    /// different behaviour. This is the same tri-state collapse the store guards against, in the
    /// opposite direction.
    /// </para>
    /// </summary>
    [Fact]
    public void Rehydrate_PreservesExplicitNullAsAPresentKey()
    {
        var source = Obj("""{ "agents": { "alpha": { "model": null } } }""");

        var rebuilt = ConfigDocumentRehydrator.Rehydrate(ConfigDocumentFlattener.Flatten(source));

        var alpha = rebuilt["agents"]!["alpha"]!.AsObject();
        alpha.ContainsKey("model").ShouldBeTrue("an explicit null must survive as a present key");
        alpha["model"].ShouldBeNull();
    }

    /// <summary>
    /// <see cref="ConfigValueState.Unset"/> is the one state a document cannot spell, so it must
    /// rehydrate to absence - never to an explicit null.
    /// </summary>
    [Fact]
    public void Rehydrate_UnsetBecomesAbsent_NotAnExplicitNull()
    {
        var entries = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal)
        {
            ["gateway.kept"] = new("gateway.kept", ConfigValueState.Value, "1"),
            ["gateway.inherited"] = new("gateway.inherited", ConfigValueState.Unset, null),
        };

        var rebuilt = ConfigDocumentRehydrator.Rehydrate(entries);

        var gateway = rebuilt["gateway"]!.AsObject();
        gateway.ContainsKey("kept").ShouldBeTrue();
        gateway.ContainsKey("inherited").ShouldBeFalse(
            "Unset means 'inherit', which a document expresses by omitting the key entirely");
    }

    [Fact]
    public void Rehydrate_PreservesEmptyObjectsAsLeaves()
    {
        var source = Obj("""{ "extensions": { "acme": {} } }""");

        var rebuilt = ConfigDocumentRehydrator.Rehydrate(ConfigDocumentFlattener.Flatten(source));

        rebuilt["extensions"]!["acme"]!.AsObject().Count.ShouldBe(0);
    }

    [Fact]
    public void Rehydrate_PreservesArraysWholesale()
    {
        var source = Obj("""{ "agents": { "alpha": { "tools": ["read", "write"] } } }""");

        var rebuilt = ConfigDocumentRehydrator.Rehydrate(ConfigDocumentFlattener.Flatten(source));

        rebuilt["agents"]!["alpha"]!["tools"]!.AsArray().Count.ShouldBe(2);
        rebuilt["agents"]!["alpha"]!["tools"]![0]!.GetValue<string>().ShouldBe("read");
    }

    /// <summary>
    /// Rehydration must be deterministic, because a parity check compares serialised text. Unstable
    /// key order would report spurious differences driven purely by row order out of SQLite.
    /// </summary>
    [Fact]
    public void Rehydrate_IsDeterministicRegardlessOfEntryOrder()
    {
        var forward = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal)
        {
            ["a.x"] = new("a.x", ConfigValueState.Value, "1"),
            ["a.y"] = new("a.y", ConfigValueState.Value, "2"),
            ["b"] = new("b", ConfigValueState.Value, "3"),
        };

        var reversed = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal)
        {
            ["b"] = new("b", ConfigValueState.Value, "3"),
            ["a.y"] = new("a.y", ConfigValueState.Value, "2"),
            ["a.x"] = new("a.x", ConfigValueState.Value, "1"),
        };

        ConfigDocumentRehydrator.Rehydrate(forward).ToJsonString()
            .ShouldBe(ConfigDocumentRehydrator.Rehydrate(reversed).ToJsonString());
    }

    /// <summary>
    /// Two stored entries describing incompatible shapes is corruption, not an ambiguity to resolve
    /// silently. Overwriting one would produce a document that round-trips cleanly having lost a key.
    /// </summary>
    [Fact]
    public void Rehydrate_ThrowsWhenStoredPathsDescribeIncompatibleShapes()
    {
        var entries = new Dictionary<string, ConfigEntry>(StringComparer.Ordinal)
        {
            ["a"] = new("a", ConfigValueState.Value, "1"),
            ["a.b"] = new("a.b", ConfigValueState.Value, "2"),
        };

        Should.Throw<InvalidOperationException>(() => ConfigDocumentRehydrator.Rehydrate(entries));
    }

    // ---------------------------------------------------------------------------------------------
    // AC7 - byte-identical dual read
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// #2766 AC7 against a REAL store: the document a consumer gets from the store is byte-identical
    /// to the one it gets from the file.
    ///
    /// <para>
    /// The fixture deliberately contains every state that has ever been a migration hazard - explicit
    /// null, empty object, array, nested scalars, an already-empty extension bag - because a parity
    /// check over a fixture containing only plain scalars would pass against a store that had lost
    /// all of them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DualRead_FileAndStore_ProduceByteIdenticalDocuments()
    {
        var source = Obj("""
            {
              "version": "1.0",
              "gateway": { "compaction": { "enabled": true, "turns": 8 }, "note": null },
              "agents": {
                "alpha": { "model": null, "tools": ["read", "write"] },
                "beta": { "model": "opus", "meta": {} }
              },
              "extensions": { "acme": { "settings": { "retries": 3 } } }
            }
            """);

        var store = CreateStore();
        await store.WriteDocumentAsync(source);
        var entries = await store.ReadEntriesAsync();

        var result = ConfigStoreRoundTripValidator.Compare(source, entries);

        result.Identical.ShouldBeTrue(
            $"the store must reproduce the document exactly.\nfile:  {result.SourceJson}\nstore: {result.StoreJson}");
    }

    /// <summary>
    /// Non-vacuity for the parity check itself: a store missing one key must be reported as NOT
    /// identical. Without this, a validator that returned <c>true</c> unconditionally would pass every
    /// other test in this class.
    /// </summary>
    [Fact]
    public void DualRead_ReportsDifference_WhenTheStoreLostAKey()
    {
        var source = Obj("""{ "gateway": { "a": 1, "b": 2 } }""");

        var lossy = ConfigDocumentFlattener.Flatten(source)
            .Where(kv => kv.Key != "gateway.b")
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        ConfigStoreRoundTripValidator.Compare(source, lossy).Identical.ShouldBeFalse();
    }

    /// <summary>
    /// The specific corruption this whole direction exists to prevent: a store that collapsed an
    /// explicit null into an absent key must fail the parity check.
    /// </summary>
    [Fact]
    public void DualRead_ReportsDifference_WhenExplicitNullCollapsedToAbsent()
    {
        var source = Obj("""{ "agents": { "alpha": { "model": null } } }""");

        var collapsed = ConfigDocumentFlattener.Flatten(source)
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.State == ConfigValueState.ExplicitNull
                    ? kv.Value with { State = ConfigValueState.Unset }
                    : kv.Value,
                StringComparer.Ordinal);

        ConfigStoreRoundTripValidator.Compare(source, collapsed).Identical.ShouldBeFalse(
            "collapsing an explicit null into unset re-enables an inherited default and must be caught");
    }

    // ---------------------------------------------------------------------------------------------
    // Read path and fallback
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ReadPath_WhenNotAuthoritative_ReadsTheFileAndNeverTouchesTheStore()
    {
        var source = Obj("""{ "gateway": { "threshold": 5 } }""");
        var store = new ThrowingConfigStore();

        var read = await CreateSource(store, source, authoritative: false).ReadAsync();

        read.Origin.ShouldBe(ConfigDocumentOrigin.File);
        read.FellBack.ShouldBeFalse();
        store.WasRead.ShouldBeFalse("the default path must not consult the store at all");
        read.Document!["gateway"]!["threshold"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public async Task ReadPath_WhenAuthoritative_ReadsFromTheStore()
    {
        var source = Obj("""{ "gateway": { "threshold": 5 } }""");
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""{ "gateway": { "threshold": 99 } }"""));

        var read = await CreateSource(store, source, authoritative: true).ReadAsync();

        read.Origin.ShouldBe(ConfigDocumentOrigin.Store);
        read.FellBack.ShouldBeFalse();
        read.Document!["gateway"]!["threshold"]!.GetValue<int>().ShouldBe(99);
    }

    /// <summary>
    /// Fails SAFE: a store that throws must not take the platform down, and must not serve an empty
    /// configuration. The file continues to serve, and the fallback is reported.
    /// </summary>
    [Fact]
    public async Task ReadPath_WhenTheStoreThrows_FallsBackToTheFileAndReportsIt()
    {
        var source = Obj("""{ "gateway": { "threshold": 5 } }""");

        var read = await CreateSource(new ThrowingConfigStore(), source, authoritative: true).ReadAsync();

        read.Origin.ShouldBe(ConfigDocumentOrigin.File);
        read.FellBack.ShouldBeTrue("a degraded read must be distinguishable from a configured one");
        read.Document!["gateway"]!["threshold"]!.GetValue<int>().ShouldBe(5);
    }

    /// <summary>
    /// An empty store is "not ready", never "an empty configuration". Serving zero rows as a real
    /// document would silently reset every setting on the platform.
    /// </summary>
    [Fact]
    public async Task ReadPath_WhenTheStoreIsEmpty_FallsBackRatherThanServingAnEmptyConfiguration()
    {
        var source = Obj("""{ "gateway": { "threshold": 5 } }""");

        var read = await CreateSource(CreateStore(), source, authoritative: true).ReadAsync();

        read.Origin.ShouldBe(ConfigDocumentOrigin.File);
        read.FellBack.ShouldBeTrue();
        read.Document!["gateway"]!["threshold"]!.GetValue<int>().ShouldBe(5);
    }

    /// <summary>
    /// End-to-end: with the store authoritative and populated by the real migration path, the document
    /// served is identical to the file's. This is the cutover rehearsal.
    /// </summary>
    [Fact]
    public async Task ReadPath_AuthoritativeStore_ServesTheSameDocumentTheFileWouldHave()
    {
        var source = Obj("""
            {
              "gateway": { "compaction": { "enabled": true }, "note": null },
              "agents": { "alpha": { "model": null, "tools": ["read"] } }
            }
            """);

        var store = CreateStore();
        await store.WriteDocumentAsync(source);

        var read = await CreateSource(store, source, authoritative: true).ReadAsync();

        read.Origin.ShouldBe(ConfigDocumentOrigin.Store);
        read.Document!.ToJsonString().ShouldBe(
            ConfigDocumentRehydrator.Rehydrate(ConfigDocumentFlattener.Flatten(source)).ToJsonString());
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures
    // ---------------------------------------------------------------------------------------------

    private static StoreBackedConfigDocumentSource CreateSource(
        IConfigStore store,
        JsonObject fileDocument,
        bool authoritative)
        => new(
            store,
            new StubShadowSource(fileDocument),
            new StubAuthoritativeGate(authoritative),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StoreBackedConfigDocumentSource>.Instance);

    private sealed class StubShadowSource(JsonObject? document) : IConfigShadowSource
    {
        public Task<JsonObject?> ReadRawDocumentAsync(CancellationToken cancellationToken)
            => Task.FromResult(document);
    }

    private sealed class StubAuthoritativeGate(bool authoritative) : IConfigStoreAuthoritativeGate
    {
        public Task<bool> IsAuthoritativeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(authoritative);
    }

    /// <summary>
    /// Records whether it was consulted, so the not-authoritative test can assert the store is not
    /// merely ignored but never touched.
    /// </summary>
    private sealed class ThrowingConfigStore : IConfigStore
    {
        public bool WasRead { get; private set; }

        public Task<IReadOnlyDictionary<string, ConfigEntry>> ReadEntriesAsync(
            CancellationToken cancellationToken = default)
        {
            WasRead = true;
            throw new InvalidOperationException("store unavailable");
        }

        public Task WriteDocumentAsync(JsonObject document, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
