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
}
