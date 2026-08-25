using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Exhaustive read/write matrix for the change-set path (#3532): every SHAPE the configuration graph
/// contains, crossed with every operation, run against BOTH backends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why shapes rather than types.</b> The diff and patch path does not care that a property is a
/// <c>ProviderConfig</c>; it cares whether the value is a scalar, an array, a keyed dictionary, an
/// opaque <see cref="JsonElement"/> bag, or a nested object - because those are what the flattener
/// treats differently. A survey of the graph yields nine shapes, and every one is exercised here.
/// Testing 42 named DTOs would be slower and would still miss the empty-object-is-a-leaf case that a
/// shape-driven survey catches immediately.
/// </para>
/// <para>
/// <b>Both backends run the same assertions.</b> A property that survives in JSON but is lost in SQLite
/// is the exact drift the single-writer design exists to prevent, and it is invisible to a test that
/// only exercises one.
/// </para>
/// </remarks>
public sealed class ConfigWriteMatrixTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _storePath;

    public ConfigWriteMatrixTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"bn-matrix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
        _storePath = Path.Combine(_directory, "config.db");
    }

    public void Dispose()
    {
        SqlitePoolCleanup.ClearPoolFor(_storePath);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle must not fail an otherwise-passing test.
        }
    }

    /// <summary>Which backend a case runs against.</summary>
    public enum Backend
    {
        Json,
        Sqlite,
    }

    /// <summary>
    /// Applies a before/after document pair through the real writer for <paramref name="backend"/> and
    /// returns what the backend holds afterwards, read back through its own read path.
    /// </summary>
    /// <remarks>
    /// Reading back through the backend's own reader rather than inspecting the change set is the whole
    /// point: a change set can be correct while the backend still fails to persist it.
    /// </remarks>
    private async Task<JsonObject> RoundTripAsync(Backend backend, JsonObject before, JsonObject after)
    {
        var changes = ConfigDocumentDiffer.Diff(before, after);

        if (backend == Backend.Json)
        {
            await File.WriteAllTextAsync(_configPath, before.ToJsonString());
            var writer = new JsonConfigurationWriter(_configPath, new FileSystem());
            await writer.ApplyChangeSetAsync(changes, "matrix");
            var text = await File.ReadAllTextAsync(_configPath);
            return JsonNode.Parse(text)!.AsObject();
        }

        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(before);
        var sqlite = new SqliteConfigurationWriter(store);
        await sqlite.ApplyChangeSetAsync(changes, "matrix");
        var entries = await store.ReadEntriesAsync();
        return ConfigDocumentRehydrator.Rehydrate(entries);
    }

    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    // -------------------------------------------------------------------------------------------
    // Shape 1: scalar value types and strings - insert, update, delete
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task Scalar_Insert_Update_And_Delete(Backend backend)
    {
        var inserted = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"port":8080}}"""),
            Doc("""{"gateway":{"port":8080,"host":"localhost"}}"""));
        inserted["gateway"]!["host"]!.GetValue<string>().ShouldBe("localhost");

        var updated = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"port":8080}}"""),
            Doc("""{"gateway":{"port":9090}}"""));
        updated["gateway"]!["port"]!.GetValue<int>().ShouldBe(9090);

        var deleted = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"port":8080,"host":"localhost"}}"""),
            Doc("""{"gateway":{"port":8080}}"""));
        deleted["gateway"]!.AsObject().ContainsKey("host").ShouldBeFalse();
    }

    /// <summary>
    /// A scalar set to JSON null must persist AS null, not vanish - absent means inherit, null means
    /// suppress, and the store's own notes call collapsing them the highest-risk failure here.
    /// </summary>
    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task Scalar_ExplicitNull_SurvivesAsNullAndIsNotDroppedToAbsent(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"agents":{"nova":{"model":"sonnet"}}}"""),
            Doc("""{"agents":{"nova":{"model":null}}}"""));

        var agent = result["agents"]!["nova"]!.AsObject();
        agent.ContainsKey("model").ShouldBeTrue("explicit null must not collapse into absence");
        agent["model"].ShouldBeNull();
    }

    // -------------------------------------------------------------------------------------------
    // Shape 2: arrays of scalars - replaced wholesale, never merged element-wise
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task ArrayOfScalars_IsReplacedWholesale_AndShrinksCorrectly(Backend backend)
    {
        // Growing.
        var grown = await RoundTripAsync(
            backend,
            Doc("""{"agents":{"nova":{"toolIds":["a"]}}}"""),
            Doc("""{"agents":{"nova":{"toolIds":["a","b","c"]}}}"""));
        grown["agents"]!["nova"]!["toolIds"]!.AsArray().Count.ShouldBe(3);

        // Shrinking is the dangerous direction: an element-wise merge would leave orphans behind.
        var shrunk = await RoundTripAsync(
            backend,
            Doc("""{"agents":{"nova":{"toolIds":["a","b","c"]}}}"""),
            Doc("""{"agents":{"nova":{"toolIds":["a"]}}}"""));
        shrunk["agents"]!["nova"]!["toolIds"]!.AsArray().Count.ShouldBe(1);

        // Reordering is one change, not N.
        var reordered = await RoundTripAsync(
            backend,
            Doc("""{"agents":{"nova":{"toolIds":["a","b"]}}}"""),
            Doc("""{"agents":{"nova":{"toolIds":["b","a"]}}}"""));
        reordered["agents"]!["nova"]!["toolIds"]!.AsArray()[0]!.GetValue<string>().ShouldBe("b");

        // Emptying an array must not be mistaken for deleting the property.
        var emptied = await RoundTripAsync(
            backend,
            Doc("""{"agents":{"nova":{"toolIds":["a"]}}}"""),
            Doc("""{"agents":{"nova":{"toolIds":[]}}}"""));
        emptied["agents"]!["nova"]!.AsObject().ContainsKey("toolIds").ShouldBeTrue();
        emptied["agents"]!["nova"]!["toolIds"]!.AsArray().Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task ArrayOfComplexObjects_SurvivesRoundTrip(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"crossWorldPermissions":[]}}"""),
            Doc("""{"gateway":{"crossWorldPermissions":[{"world":"w1","allow":true}]}}"""));

        var arr = result["gateway"]!["crossWorldPermissions"]!.AsArray();
        arr.Count.ShouldBe(1);
        arr[0]!["world"]!.GetValue<string>().ShouldBe("w1");
    }

    // -------------------------------------------------------------------------------------------
    // Shape 3: keyed dictionaries - the eight where deletion is only visible as absence
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task KeyedDictionary_AddSecondEntry_LeavesTheFirstIntact(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"agents":{"nova":{"model":"sonnet","apiKey":"secret-1"}}}"""),
            Doc("""{"agents":{"nova":{"model":"sonnet","apiKey":"secret-1"},"farnsworth":{"model":"opus"}}}"""));

        result["agents"]!["nova"]!["apiKey"]!.GetValue<string>().ShouldBe("secret-1");
        result["agents"]!["farnsworth"]!["model"]!.GetValue<string>().ShouldBe("opus");
    }

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task KeyedDictionary_RemoveOneEntry_LeavesSiblingsIntact(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"agents":{"nova":{"model":"sonnet"},"retired":{"model":"old"}}}"""),
            Doc("""{"agents":{"nova":{"model":"sonnet"}}}"""));

        result["agents"]!.AsObject().ContainsKey("retired").ShouldBeFalse();
        result["agents"]!["nova"]!["model"]!.GetValue<string>().ShouldBe("sonnet");
    }

    /// <summary>
    /// The empty-section case that broke the Gateway suite: an empty object is a LEAF to the flattener,
    /// so populating it emits both an upsert beneath it and a removal of it.
    /// </summary>
    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task KeyedDictionary_PopulatingAnEmptySection_DoesNotDeleteWhatWasJustWritten(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"locations":{}}}"""),
            Doc("""{"gateway":{"locations":{"repo":{"type":"filesystem","path":"/tmp/repo"}}}}"""));

        result["gateway"]!["locations"]!["repo"]!["type"]!.GetValue<string>().ShouldBe("filesystem");
    }

    /// <summary>
    /// And the reverse: emptying a populated section must leave the section present but empty, not
    /// delete the section itself.
    /// </summary>
    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task KeyedDictionary_EmptyingASection_KeepsTheSectionAsAnEmptyObject(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"locations":{"repo":{"type":"filesystem"}}}}"""),
            Doc("""{"gateway":{"locations":{}}}"""));

        result["gateway"]!.AsObject().ContainsKey("locations").ShouldBeTrue();
        result["gateway"]!["locations"]!.AsObject().Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task DictionaryOfStrings_RoundTripsAndRemovesIndividualKeys(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"locations":{"repo":{"properties":{"a":"1","b":"2"}}}}}"""),
            Doc("""{"gateway":{"locations":{"repo":{"properties":{"a":"1"}}}}}"""));

        var props = result["gateway"]!["locations"]!["repo"]!["properties"]!.AsObject();
        props.ContainsKey("b").ShouldBeFalse();
        props["a"]!.GetValue<string>().ShouldBe("1");
    }

    // -------------------------------------------------------------------------------------------
    // Shape 4: opaque JsonElement bags - extension configs with no CLR schema at all
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Extension config is an open-world bag: nothing models its interior, so a typed round-trip would
    /// erase it entirely. It must survive byte-for-byte through the change-set path.
    /// </summary>
    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task OpaqueJsonElementBag_SurvivesUnmodelledNestedContent(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"agents":{"nova":{"extensions":{"mcp":{"servers":{"x":{"cmd":"run","args":["--flag"]}}}}}}}"""),
            Doc("""{"agents":{"nova":{"model":"opus","extensions":{"mcp":{"servers":{"x":{"cmd":"run","args":["--flag"]}}}}}}}"""));

        result["agents"]!["nova"]!["model"]!.GetValue<string>().ShouldBe("opus");

        // The bag is untouched, including content no CLR type has ever seen.
        var args = result["agents"]!["nova"]!["extensions"]!["mcp"]!["servers"]!["x"]!["args"]!.AsArray();
        args[0]!.GetValue<string>().ShouldBe("--flag");
    }

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task DeeplyNestedObject_ChangesOnlyTheTargetedLeaf(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"compaction":{"enabled":true,"memoryFlush":{"enabled":false,"threshold":10}}}}"""),
            Doc("""{"gateway":{"compaction":{"enabled":true,"memoryFlush":{"enabled":true,"threshold":10}}}}"""));

        result["gateway"]!["compaction"]!["memoryFlush"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        result["gateway"]!["compaction"]!["memoryFlush"]!["threshold"]!.GetValue<int>().ShouldBe(10);
        result["gateway"]!["compaction"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    // -------------------------------------------------------------------------------------------
    // Shape transitions: a key changing KIND, which is where a naive differ loses data
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task ScalarBecomingAnObject_ReplacesTheLeafWithTheBranch(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"auth":"none"}}"""),
            Doc("""{"gateway":{"auth":{"type":"apikey","required":true}}}"""));

        result["gateway"]!["auth"]!["type"]!.GetValue<string>().ShouldBe("apikey");
    }

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task ObjectBecomingAScalar_ReplacesTheBranchWithTheLeaf(Backend backend)
    {
        var result = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"auth":{"type":"apikey","required":true}}}"""),
            Doc("""{"gateway":{"auth":"none"}}"""));

        result["gateway"]!["auth"]!.GetValue<string>().ShouldBe("none");
    }

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task ArrayBecomingAnObject_AndBack(Backend backend)
    {
        var toObject = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"shellCommand":["pwsh","-c"]}}"""),
            Doc("""{"gateway":{"shellCommand":{"exe":"pwsh"}}}"""));
        toObject["gateway"]!["shellCommand"]!["exe"]!.GetValue<string>().ShouldBe("pwsh");

        var toArray = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"shellCommand":{"exe":"pwsh"}}}"""),
            Doc("""{"gateway":{"shellCommand":["pwsh","-c"]}}"""));
        toArray["gateway"]!["shellCommand"]!.AsArray().Count.ShouldBe(2);
    }

    // -------------------------------------------------------------------------------------------
    // Root-level and whole-document properties
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task RootLevelSection_CanBeAddedAndRemoved(Backend backend)
    {
        var added = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"port":8080}}"""),
            Doc("""{"gateway":{"port":8080},"channels":{"telegram":{"enabled":true}}}"""));
        added["channels"]!["telegram"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();

        var removed = await RoundTripAsync(
            backend,
            Doc("""{"gateway":{"port":8080},"channels":{"telegram":{"enabled":true}}}"""),
            Doc("""{"gateway":{"port":8080}}"""));
        removed.ContainsKey("channels").ShouldBeFalse();
    }

    /// <summary>
    /// The full-graph property: a realistic document with every shape present must survive a no-op
    /// apply completely unchanged.
    /// </summary>
    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task FullGraph_NoOpApply_IsByteIdentical(Backend backend)
    {
        var document = Doc("""
            {
              "gateway": {
                "port": 8080,
                "shellCommand": ["pwsh","-c"],
                "compaction": { "enabled": true, "memoryFlush": { "enabled": false } },
                "locations": { "repo": { "type": "filesystem", "properties": { "a": "1" } } },
                "crossWorldPermissions": [ { "world": "w1" } ]
              },
              "agents": {
                "nova": {
                  "model": "sonnet",
                  "toolIds": ["a","b"],
                  "suppressed": null,
                  "extensions": { "mcp": { "servers": {} } }
                }
              },
              "channels": { "telegram": { "enabled": true, "botToken": "tg-secret" } },
              "featureManagement": { "SomeFlag": true }
            }
            """);

        var result = await RoundTripAsync(backend, document, document.DeepClone().AsObject());

        var expected = ConfigDocumentFlattener.Flatten(document);
        var actual = ConfigDocumentFlattener.Flatten(result);

        actual.Count.ShouldBe(expected.Count);
        foreach (var (path, entry) in expected)
        {
            actual.ShouldContainKey(path);
            actual[path].State.ShouldBe(entry.State);
            actual[path].Value.ShouldBe(entry.Value);
        }
    }

    /// <summary>
    /// A single targeted edit against the full graph must move exactly one key and leave every other
    /// key - including every secret - byte-identical.
    /// </summary>
    [Theory]
    [InlineData(Backend.Json)]
    [InlineData(Backend.Sqlite)]
    public async Task FullGraph_SingleEdit_MovesExactlyOneKey(Backend backend)
    {
        var before = Doc("""
            {
              "gateway": { "port": 8080, "compaction": { "enabled": true } },
              "agents": { "nova": { "model": "sonnet", "toolIds": ["a"] } },
              "channels": { "telegram": { "enabled": false, "botToken": "tg-secret" } },
              "providers": { "anthropic": { "apiKey": "sk-real", "models": ["opus"] } }
            }
            """);

        var after = before.DeepClone().AsObject();
        after["channels"]!["telegram"]!["enabled"] = true;

        var changes = ConfigDocumentDiffer.Diff(before, after);
        changes.Upserts.ShouldHaveSingleItem().Path.ShouldBe("channels.telegram.enabled");
        changes.Removals.ShouldBeEmpty();

        var result = await RoundTripAsync(backend, before, after);

        result["channels"]!["telegram"]!["enabled"]!.GetValue<bool>().ShouldBeTrue();
        result["channels"]!["telegram"]!["botToken"]!.GetValue<string>().ShouldBe("tg-secret");
        result["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-real");
        result["agents"]!["nova"]!["toolIds"]!.AsArray().Count.ShouldBe(1);
    }
}
