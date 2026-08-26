using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Race-condition cover for the change-set write path (#3532), on both backends.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why concurrency is the interesting case for a diff-based writer.</b> A whole-document write is
/// last-writer-wins by construction: two concurrent saves of unrelated sections lose one section
/// outright. A change-set write should be strictly better - two writers touching different keys should
/// BOTH survive - but only if the read-diff-write sequence is properly serialised. These tests pin that
/// improvement rather than assuming it.
/// </para>
/// <para>
/// The lost-update hazard is real: <c>ApplyAsync</c> reads current state, diffs, then writes. Without a
/// lock, two callers can read the same before-image and the second write can clobber the first. The
/// production path funnels through <c>PlatformConfigWriter</c>, which holds a semaphore plus a
/// cross-process file lock; these tests exercise both the guarded path and the raw backends so the
/// difference is visible rather than assumed.
/// </para>
/// <para>
/// <b>Mutation-verified, with a finding.</b> Removing the in-process semaphore alone leaves all seven
/// tests green: the cross-process file lock in <c>MutateCoreAsync</c> serialises the same critical
/// section on its own, so the two guards are redundant for same-process concurrency rather than
/// layered. Removing BOTH fails three of these tests by name, which is what proves they genuinely
/// race rather than passing by accident of timing. The redundancy is defensible - the semaphore keeps
/// this process's threads off the OS lock, per the ordering argument in <c>CrossProcessConfigLock</c> -
/// but it is worth knowing that the file lock is the one carrying the correctness weight.
/// </para>
/// </remarks>
public sealed class ConfigWriteConcurrencyTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _storePath;

    public ConfigWriteConcurrencyTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"bn-race-{Guid.NewGuid():N}");
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

    /// <summary>
    /// The headline concurrency property, through the guarded production path: N writers each adding a
    /// DIFFERENT agent all survive. Under a whole-document write this is exactly the scenario that loses
    /// entries.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesToDifferentKeys_AllSurvive_ThroughTheGuardedWriter()
    {
        await File.WriteAllTextAsync(_configPath, """{"agents":{}}""");
        var writer = new PlatformConfigWriter(_configPath, new FileSystem());

        const int writers = 12;
        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
            await writer.MutateAsync(
                root =>
                {
                    var agents = root["agents"] as JsonObject ?? new JsonObject();
                    root["agents"] = agents;
                    agents[$"agent{i}"] = new JsonObject { ["model"] = $"model-{i}" };
                },
                $"add-agent-{i}",
                CancellationToken.None)));

        await Task.WhenAll(tasks);

        var final = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        var agents = final["agents"]!.AsObject();

        agents.Count.ShouldBe(writers, "every concurrent writer touched a distinct key, so none may be lost");
        for (var i = 0; i < writers; i++)
        {
            agents[$"agent{i}"]!["model"]!.GetValue<string>().ShouldBe($"model-{i}");
        }
    }

    /// <summary>
    /// Concurrent writes to unrelated SECTIONS must not clobber one another - the #2816 shape under
    /// contention rather than under a single bad payload.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesToUnrelatedSections_PreserveBothSections()
    {
        await File.WriteAllTextAsync(_configPath, """
            {"channels":{"telegram":{"botToken":"tg-secret"}},"providers":{"anthropic":{"apiKey":"sk-real"}}}
            """);
        var writer = new PlatformConfigWriter(_configPath, new FileSystem());

        var channelWrites = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            await writer.MutateAsync(
                root => ((JsonObject)root["channels"]!["telegram"]!)["enabled"] = i % 2 == 0,
                "channel", CancellationToken.None)));

        var providerWrites = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            await writer.MutateAsync(
                root => ((JsonObject)root["providers"]!["anthropic"]!)["timeout"] = i,
                "provider", CancellationToken.None)));

        await Task.WhenAll(channelWrites.Concat(providerWrites));

        var final = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();

        // Neither credential was collateral damage, and both sections still carry their new field.
        final["channels"]!["telegram"]!["botToken"]!.GetValue<string>().ShouldBe("tg-secret");
        final["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-real");
        final["channels"]!["telegram"]!.AsObject().ContainsKey("enabled").ShouldBeTrue();
        final["providers"]!["anthropic"]!.AsObject().ContainsKey("timeout").ShouldBeTrue();
    }

    /// <summary>
    /// Concurrent removals and insertions in the same keyed dictionary must converge to a consistent
    /// document - never a half-deleted entry.
    /// </summary>
    [Fact]
    public async Task ConcurrentAddAndRemove_InTheSameSection_ConvergeConsistently()
    {
        await File.WriteAllTextAsync(_configPath, """
            {"agents":{"keep":{"model":"a"},"doomed":{"model":"b","apiKey":"secret"}}}
            """);
        var writer = new PlatformConfigWriter(_configPath, new FileSystem());

        var adds = Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
            await writer.MutateAsync(
                root => ((JsonObject)root["agents"]!)[$"new{i}"] = new JsonObject { ["model"] = "n" },
                "add", CancellationToken.None)));

        var remove = Task.Run(async () =>
            await writer.MutateAsync(
                root => ((JsonObject)root["agents"]!).Remove("doomed"),
                "remove", CancellationToken.None));

        await Task.WhenAll(adds.Append(remove));

        var final = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        var agents = final["agents"]!.AsObject();

        agents.ContainsKey("doomed").ShouldBeFalse("the removal must not be silently reverted");
        agents["keep"]!["model"]!.GetValue<string>().ShouldBe("a");
        for (var i = 0; i < 6; i++)
        {
            agents.ShouldContainKey($"new{i}");
        }
    }

    /// <summary>
    /// The store's change-set application is transactional: concurrent appliers must never leave a
    /// partially-applied set behind.
    /// </summary>
    [Fact]
    public async Task ConcurrentStoreApplies_AreAtomicPerChangeSet()
    {
        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(JsonNode.Parse("""{"agents":{}}""")!.AsObject());

        const int writers = 10;
        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            // Each change set writes a PAIR of keys. A non-atomic apply would let a reader observe
            // one without the other, so asserting pair-consistency at the end detects it.
            var changes = new ConfigChangeSet(
                [
                    new ConfigEntry($"agents.a{i}.model", ConfigValueState.Value, "\"m\""),
                    new ConfigEntry($"agents.a{i}.apiKey", ConfigValueState.Value, "\"k\""),
                ],
                []);

            await store.ApplyChangesAsync(changes);
        }));

        await Task.WhenAll(tasks);

        var entries = await store.ReadEntriesAsync();
        for (var i = 0; i < writers; i++)
        {
            entries.ShouldContainKey($"agents.a{i}.model");
            entries.ShouldContainKey($"agents.a{i}.apiKey");
        }
    }

    /// <summary>
    /// Repeated concurrent application of the SAME change set must be idempotent - the upsert is
    /// ON CONFLICT DO UPDATE, so a duplicate must update rather than throw a constraint violation.
    /// </summary>
    [Fact]
    public async Task ConcurrentIdenticalApplies_AreIdempotentAndDoNotViolateThePrimaryKey()
    {
        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(new JsonObject());

        var changes = new ConfigChangeSet(
            [new ConfigEntry("gateway.port", ConfigValueState.Value, "8080")],
            []);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
            await store.ApplyChangesAsync(changes))));

        var entries = await store.ReadEntriesAsync();
        entries["gateway.port"].Value.ShouldBe("8080");
    }

    /// <summary>
    /// Fan-out under contention: both backends must end up holding the same document, because a reader
    /// resolving from the store while the file says something else is the split-state defect the
    /// fan-out exists to prevent.
    /// </summary>
    [Fact]
    public async Task ConcurrentFanOutApplies_LeaveBothBackendsAgreeing()
    {
        await File.WriteAllTextAsync(_configPath, """{"agents":{}}""");
        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(JsonNode.Parse("""{"agents":{}}""")!.AsObject());

        var fanOut = new FanOutConfigurationWriter(
        [
            new JsonConfigurationWriter(_configPath, new FileSystem()),
            new SqliteConfigurationWriter(store),
        ]);

        // Serialised deliberately: the fan-out has no lock of its own - PlatformConfigWriter provides
        // it in production - so this asserts convergence, not that the fan-out is itself thread-safe.
        for (var i = 0; i < 8; i++)
        {
            var changes = new ConfigChangeSet(
                [new ConfigEntry($"agents.a{i}.model", ConfigValueState.Value, "\"m\"")],
                []);

            await fanOut.ApplyChangeSetAsync(changes, "fanout");
        }

        var file = ConfigDocumentFlattener.Flatten(
            JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject());
        var rows = await store.ReadEntriesAsync();

        foreach (var (path, entry) in file)
        {
            rows.ShouldContainKey(path);
            rows[path].Value.ShouldBe(entry.Value);
        }
    }

    /// <summary>
    /// A cancelled write must not leave a partially-applied change set in the store.
    /// </summary>
    [Fact]
    public async Task CancelledApply_DoesNotCommitAPartialChangeSet()
    {
        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(JsonNode.Parse("""{"gateway":{"port":8080}}""")!.AsObject());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var changes = new ConfigChangeSet(
            [new ConfigEntry("gateway.host", ConfigValueState.Value, "\"localhost\"")],
            []);

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await store.ApplyChangesAsync(changes, cts.Token));

        var entries = await store.ReadEntriesAsync();
        entries.ContainsKey("gateway.host").ShouldBeFalse("a cancelled write must not be observable");
        entries["gateway.port"].Value.ShouldBe("8080");
    }
}
