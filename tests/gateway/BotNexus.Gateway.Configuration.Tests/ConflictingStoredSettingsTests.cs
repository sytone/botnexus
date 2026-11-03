using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>Stored leaves cannot also be ancestors, even when their JSON value is an object or null.</summary>
public sealed class ConflictingStoredSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"bn-conflicting-settings-{Guid.NewGuid():N}");
    private readonly IFileSystem _fileSystem = new FileSystem();
    private string ConfigPath => Path.Combine(_directory, "config.json");
    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(ConfigPath, _fileSystem);
    private const string Seed = """{"version":1,"gateway":{"listenUrl":"http://localhost:5000"},"untouched":{"token":"canonical"}}""";
    private const string Projection = """{"version":99,"gateway":{"listenUrl":"http://localhost:9999"},"a":{"projectionOnly":true}}""";

    public ConflictingStoredSettingsTests() => Directory.CreateDirectory(_directory);

    /// <summary>Crosses every parent shape with depth, insertion order, and a nonadjacent sorted descendant.</summary>
    public static IEnumerable<object?[]> CollisionCases()
    {
        foreach (var (state, value) in ParentLeaves())
        foreach (var descendant in new[] { "a.b", "a.b.c" })
        foreach (var reverse in new[] { false, true })
        foreach (var interveningSibling in new[] { false, true })
            yield return [state, value, descendant, reverse, interveningSibling];
    }

    private static IEnumerable<(ConfigValueState State, string? Value)> ParentLeaves()
    {
        yield return (ConfigValueState.ExplicitNull, null);
        yield return (ConfigValueState.Value, "null");
        yield return (ConfigValueState.Value, "{}");
        yield return (ConfigValueState.Value, "{\"kept\":1}");
        yield return (ConfigValueState.Value, "{\"b\":{\"kept\":1}}");
        yield return (ConfigValueState.Value, "[1,null,{}]");
        yield return (ConfigValueState.Value, "1");
    }

    [Theory]
    [MemberData(nameof(CollisionCases))]
    public void Rehydrate_StoredLeafAndDescendant_RejectsRatherThanMergingOrOverwriting(
        ConfigValueState state, string? value, string descendant, bool reverse, bool interveningSibling)
    {
        var entries = ConflictingEntries(state, value, descendant, reverse, interveningSibling);
        var before = entries.ToArray();

        Should.Throw<InvalidOperationException>(() => ConfigDocumentRehydrator.Rehydrate(entries));

        entries.ToArray().ShouldBe(before);
    }

    [Theory]
    [MemberData(nameof(CollisionCases))]
    public async Task ColdBootstrap_ConflictingRows_RejectsValidProjectionWithoutChangingAuthority(
        ConfigValueState state, string? value, string descendant, bool reverse, bool interveningSibling)
    {
        var store = await InitializeAsync();
        await SeedConflictAsync(store, state, value, descendant, reverse, interveningSibling);
        var before = await store.ReadSnapshotAsync();

        var error = await Record.ExceptionAsync(async () =>
            await ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, _fileSystem));

        await AssertUnchangedAsync(store, before);
        error.ShouldBeOfType<InvalidOperationException>();
    }

    [Theory]
    [MemberData(nameof(CollisionCases))]
    public async Task ColdProvider_ConflictingRows_RejectsInsteadOfPublishingProjectionOrPartialData(
        ConfigValueState state, string? value, string descendant, bool reverse, bool interveningSibling)
    {
        var store = await InitializeAsync();
        await SeedConflictAsync(store, state, value, descendant, reverse, interveningSibling);
        var before = await store.ReadSnapshotAsync();
        // A fresh physical store and provider prove initial-load behavior, not last-known-good reload.
        using var provider = new SqliteConfigurationProvider(
            new SqliteConfigStore($"Data Source={StorePath}"), startChangeDetection: false);

        var error = Record.Exception(provider.Load);

        await AssertUnchangedAsync(store, before);
        error.ShouldBeOfType<InvalidDataException>().InnerException.ShouldBeOfType<InvalidOperationException>();
        provider.TryGet("version", out _).ShouldBeFalse();
        provider.TryGet("a:b", out _).ShouldBeFalse();
        provider.TryGet("a:projectionOnly", out _).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(CollisionCases))]
    public async Task CanonicalMutation_ConflictingRows_ThrowsBeforeMutatorAndPreservesRowsRevisionAndProjection(
        ConfigValueState state, string? value, string descendant, bool reverse, bool interveningSibling)
    {
        var store = await InitializeAsync();
        // Create the real canonical writer while authority is valid; corruption arrives afterwards.
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        AssertSqliteAuthority(writer);
        await SeedConflictAsync(store, state, value, descendant, reverse, interveningSibling);
        var before = await store.ReadSnapshotAsync();
        var mutatorCalled = false;

        ConfigMutationResult? outcome = null;
        var error = await Record.ExceptionAsync(async () => outcome = await writer.MutateWithResultAsync(root =>
        {
            mutatorCalled = true;
            root["version"] = 2;
        }, "reject-conflicting-settings"));

        await AssertUnchangedAsync(store, before);
        mutatorCalled.ShouldBeFalse();
        error.ShouldBeOfType<InvalidOperationException>();
        outcome.ShouldBeNull();
    }

    [Theory]
    [InlineData(ConfigValueState.Unset, "a.b", false)]
    [InlineData(ConfigValueState.Unset, "a.b", true)]
    [InlineData(ConfigValueState.Unset, "a.b.c", false)]
    [InlineData(ConfigValueState.Unset, "a.b.c", true)]
    [InlineData(ConfigValueState.Unknown, "a.b", false)]
    [InlineData(ConfigValueState.Unknown, "a.b", true)]
    [InlineData(ConfigValueState.Unknown, "a.b.c", false)]
    [InlineData(ConfigValueState.Unknown, "a.b.c", true)]
    public void Rehydrate_AbsentAncestor_DoesNotConflictWithPresentDescendant(
        ConfigValueState state, string descendant, bool reverse)
    {
        var entries = ConflictingEntries(state, null, descendant, reverse, interveningSibling: true);

        var rebuilt = ConfigDocumentRehydrator.Rehydrate(entries);

        var expected = descendant == "a.b"
            ? Parse("""{"a":{"b":2},"a-other":3}""")
            : Parse("""{"a":{"b":{"c":2}},"a-other":3}""");
        JsonNode.DeepEquals(rebuilt, expected).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ConfigValueState.Unset)]
    [InlineData(ConfigValueState.Unknown)]
    public void Rehydrate_AbsentDescendant_DoesNotConflictWithAnyPresentParent(ConfigValueState absentState)
    {
        foreach (var (state, value) in ParentLeaves())
        foreach (var reverse in new[] { false, true })
        {
            var leaves = new[] { new ConfigEntry("a", state, value), new ConfigEntry("a.b.c", absentState, null) };
            var entries = (reverse ? leaves.Reverse() : leaves).ToDictionary(entry => entry.Path);

            var rebuilt = ConfigDocumentRehydrator.Rehydrate(entries);

            rebuilt.ContainsKey("a").ShouldBeTrue();
            rebuilt.Count.ShouldBe(1);
            JsonNode.DeepEquals(rebuilt["a"], state == ConfigValueState.ExplicitNull
                ? null : JsonNode.Parse(value ?? "null")).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Model.1\":{\"contextWindow\":1000}}")]
    public void Rehydrate_CompleteModelCapacityMapAndStoredDescendant_RejectsCollision(string map)
    {
        const string path = "providers.custom.chat.modelCapacities";
        var entries = new Dictionary<string, ConfigEntry>
        {
            [path] = new(path, ConfigValueState.Value, map),
            [path + ".extra.contextWindow"] = new(path + ".extra.contextWindow", ConfigValueState.Value, "2000")
        };

        Should.Throw<InvalidOperationException>(() => ConfigDocumentRehydrator.Rehydrate(entries));
    }

    [Fact]
    public async Task ValidSiblingsAndCompleteLeaves_RoundTripThroughPhysicalAuthorityAndProvider()
    {
        var document = Parse("""
            {"version":1,"a":{"b":2,"c":3},"a-other":4,"empty":{},"items":[1,null,{}],
             "explicitNull":null,"providers":{"custom":{"chat":{"defaultModel":"Model.1",
             "modelCapacities":{"Model.1":{"contextWindow":1000,"maxTokens":100},
             "model.1":{"contextWindow":2000},"vendor:model":{"contextWindow":3000}}}}}}
            """);
        await File.WriteAllTextAsync(ConfigPath, document.ToJsonString());
        var store = await ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, _fileSystem);
        File.Exists(StorePath).ShouldBeTrue();
        var snapshot = await store.ReadSnapshotAsync();
        snapshot.Entries["empty"].Value.ShouldBe("{}");
        snapshot.Entries["items"].Value.ShouldBe("[1,null,{}]");
        snapshot.Entries["providers.custom.chat.modelCapacities"].State.ShouldBe(ConfigValueState.Value);
        snapshot.Entries.Keys.ShouldNotContain("providers.custom.chat.modelCapacities.Model.1.contextWindow");
        JsonNode.DeepEquals(ConfigDocumentRehydrator.Rehydrate(snapshot.Entries), document).ShouldBeTrue();
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        AssertSqliteAuthority(writer);
        JsonNode.DeepEquals(await writer.ReadAsync(), document).ShouldBeTrue();
        using var provider = new SqliteConfigurationProvider(store, startChangeDetection: false);
        provider.Load();
        provider.TryGet("a:b", out var sibling).ShouldBeTrue();
        sibling.ShouldBe("2");
        provider.TryGet("items:0", out var item).ShouldBeTrue();
        item.ShouldBe("1");
        using var configuration = new ConfigurationBuilder().Add(new SqliteConfigurationSource
        {
            Store = store, StartChangeDetection = false
        }).Build().ShouldBeOfType<ConfigurationRoot>();
        configuration.Providers.ShouldHaveSingleItem().ShouldBeOfType<SqliteConfigurationProvider>();
        var services = new ServiceCollection();
        services.Configure<PlatformConfig>(configuration);
        services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(new PlatformConfigPostConfigure(configuration));
        using var serviceProvider = services.BuildServiceProvider();
        var monitor = serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
        var capacities = monitor.CurrentValue.Providers.ShouldNotBeNull()["custom"].Chat
            .ShouldNotBeNull().ModelCapacities.ShouldNotBeNull();
        capacities.Count.ShouldBe(3);
        capacities["Model.1"].ContextWindow.ShouldBe(1000);
        capacities["Model.1"].MaxTokens.ShouldBe(100);
        capacities["model.1"].ContextWindow.ShouldBe(2000);
        capacities["vendor:model"].ContextWindow.ShouldBe(3000);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("1")]
    public async Task CanonicalMutation_LeafBecomesObject_RemovesOldRowBeforeUpsertingDescendants(string leaf)
    {
        var initial = Parse(Seed);
        initial["a"] = JsonNode.Parse(leaf);
        await File.WriteAllTextAsync(ConfigPath, initial.ToJsonString());
        var store = await ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, _fileSystem);
        var writer = ConfigWriterFactory.Create(ConfigPath, _fileSystem);
        AssertSqliteAuthority(writer);
        var before = await store.ReadSnapshotAsync();
        var expected = initial.DeepClone().AsObject();
        expected["a"] = Parse("""{"b":{"c":2},"sibling":3}""");
        var changes = ConfigDocumentDiffer.Diff(initial, expected);
        changes.Removals.ShouldContain("a");
        changes.Upserts.Select(entry => entry.Path).ShouldBe(new[] { "a.b.c", "a.sibling" });
        var patched = initial.DeepClone().AsObject();
        patched.Apply(changes);
        JsonNode.DeepEquals(patched, expected).ShouldBeTrue();

        await writer.MutateAsync(root => root["a"] = expected["a"].ShouldNotBeNull().DeepClone(),
            "valid-leaf-to-object");

        var after = await store.ReadSnapshotAsync();
        after.Revision.ShouldBe(before.Revision + 1);
        after.Entries.ShouldNotContainKey("a");
        after.Entries["a.b.c"].Value.ShouldBe("2");
        after.Entries["a.sibling"].Value.ShouldBe("3");
        foreach (var (path, entry) in before.Entries.Where(pair => pair.Key != "a"))
            after.Entries[path].ShouldBe(entry);
        JsonNode.DeepEquals(ConfigDocumentRehydrator.Rehydrate(after.Entries), expected).ShouldBeTrue();
        JsonNode.DeepEquals(await writer.ReadAsync(), expected).ShouldBeTrue();
        JsonNode.DeepEquals(Parse(await File.ReadAllTextAsync(ConfigPath)), expected).ShouldBeTrue();
    }

    private async Task<IConfigStore> InitializeAsync()
    {
        await File.WriteAllTextAsync(ConfigPath, Seed);
        var store = await ConfigStoreBootstrap.EnsureAuthorityAsync(ConfigPath, _fileSystem);
        File.Exists(StorePath).ShouldBeTrue();
        JsonNode.DeepEquals(ConfigDocumentRehydrator.Rehydrate(await store.ReadEntriesAsync()), Parse(Seed)).ShouldBeTrue();
        return store;
    }

    private async Task SeedConflictAsync(IConfigStore store, ConfigValueState state, string? value,
        string descendant, bool reverse, bool interveningSibling)
    {
        var entries = ConflictingEntries(state, value, descendant, reverse, interveningSibling);
        await store.ApplyChangesAsync(new ConfigChangeSet(entries.Values.ToArray(), []));
        var seeded = await store.ReadEntriesAsync();
        foreach (var (path, entry) in entries)
            seeded[path].ShouldBe(entry);
        // This valid but contradictory document must never become a fallback read source.
        await File.WriteAllTextAsync(ConfigPath, Projection);
    }

    private async Task AssertUnchangedAsync(IConfigStore store, ConfigStoreSnapshot before)
    {
        var after = await store.ReadSnapshotAsync();
        after.Revision.ShouldBe(before.Revision);
        after.Entries.Count.ShouldBe(before.Entries.Count);
        foreach (var (path, entry) in before.Entries)
            after.Entries[path].ShouldBe(entry);
        (await File.ReadAllBytesAsync(ConfigPath)).ShouldBe(System.Text.Encoding.UTF8.GetBytes(Projection));
    }

    private void AssertSqliteAuthority(PlatformConfigWriter writer)
    {
        var backend = writer.Backends.ShouldHaveSingleItem();
        backend.Name.ShouldBe("sqlite");
        backend.Location.ShouldBe(StorePath);
        backend.WinsOnRead.ShouldBeTrue();
    }

    private static Dictionary<string, ConfigEntry> ConflictingEntries(ConfigValueState state, string? value,
        string descendant, bool reverse, bool interveningSibling)
    {
        var leaves = new List<ConfigEntry> { new("a", state, value) };
        if (interveningSibling)
            leaves.Add(new("a-other", ConfigValueState.Value, "3"));
        leaves.Add(new(descendant, ConfigValueState.Value, "2"));
        if (reverse)
            leaves.Reverse();
        return leaves.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
    }

    private static JsonObject Parse(string text) => JsonNode.Parse(text).ShouldBeOfType<JsonObject>();

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        Directory.Delete(_directory, recursive: true);
    }
}
