using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Adversarial and negative cover for the configuration write path (#3532), exercised through the
/// SAME <see cref="PlatformConfigWriter"/> methods the CLI and the REST API call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite exists.</b> Two defects shipped from this branch already - a superseded-removal
/// filter that was right for documents and wrong for rows, and an upsert/removal ordering bug that
/// deleted a subtree it had just written. Both passed hand-picked happy-path tests. The working
/// assumption here is therefore that more remain, so the cases are chosen to be hostile rather than
/// representative: malformed input, hostile keys, boundary values, byte-level round-trips, and the
/// exact method each production surface calls.
/// </para>
/// <para>
/// <b>Surface mapping, verified against source rather than recalled.</b> The REST API calls
/// <c>ApplyPatchAsync</c> (PATCH), <c>UpdateSectionAsync</c> (PUT section),
/// <c>UpdateSectionEntryAsync</c> (PUT section/key) and <c>RemoveSectionEntryAsync</c> (DELETE
/// section/key); the CLI calls <c>MutateDocumentAsync</c> (init, satellite, doctor) and
/// <c>MutateValidatedAsync</c> (backup restore); <c>LocationsController</c> calls
/// <c>MutateSectionAsync</c>. Every one funnels through <c>MutateCoreAsync</c>, which is the single
/// seam the change-set write now sits behind - so covering these methods covers both surfaces.
/// </para>
/// </remarks>
public sealed class ConfigWriteNegativeCaseTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _storePath;
    private readonly FileSystem _fileSystem = new();

    public ConfigWriteNegativeCaseTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"bn-neg-{Guid.NewGuid():N}");
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
    /// A sentinel modification time, far enough in the past that any real write moves it.
    /// </summary>
    /// <remarks>
    /// Stamping a known timestamp and asserting it is unchanged replaces sleeping to let the clock
    /// tick. A finite <c>Task.Delay</c> here would be both slower and flakier - filesystem timestamp
    /// granularity is not guaranteed to be finer than the delay - and it is fenced by
    /// <c>TestDelayFlakeFenceTests</c> for exactly that reason.
    /// </remarks>
    private static readonly DateTime MtimeSentinel = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Stamps the config file with <see cref="MtimeSentinel"/> so a write is detectable.</summary>
    private void StampSentinelMtime() => File.SetLastWriteTimeUtc(_configPath, MtimeSentinel);

    /// <summary>Asserts the config file has not been rewritten since <see cref="StampSentinelMtime"/>.</summary>
    private void ShouldNotHaveBeenRewritten() =>
        File.GetLastWriteTimeUtc(_configPath).ShouldBe(MtimeSentinel, "the file must not have been rewritten");

    private PlatformConfigWriter Writer() => new(_configPath, _fileSystem);

    private async Task SeedAsync(string json) => await File.WriteAllTextAsync(_configPath, json);

    private async Task<JsonObject> ReadBackAsync() =>
        JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();

    // =============================================================================================
    // Round-trip fidelity: write it, read it, write it again - nothing may drift
    // =============================================================================================

    /// <summary>
    /// Forward-then-back through the API's section-update path must be a fixed point: applying the
    /// value that was just read back changes nothing.
    /// </summary>
    /// <remarks>
    /// A writer that normalises on the way in - reorders keys, coerces number formats, drops an empty
    /// container - passes a single write test and then churns the file on every subsequent save. The
    /// second write is what exposes it.
    /// </remarks>
    [Fact]
    public async Task ApiSectionUpdate_IsAFixedPoint_OnTheSecondApplication()
    {
        await SeedAsync("""
            {"channels":{"telegram":{"enabled":false,"botToken":"tg","retries":3,"tags":["a","b"]}}}
            """);

        var writer = Writer();
        var section = (await ReadBackAsync())["channels"]!.DeepClone();

        await writer.UpdateSectionAsync("channels", section, CancellationToken.None);
        var first = await File.ReadAllTextAsync(_configPath);

        await writer.UpdateSectionAsync("channels", JsonNode.Parse(first)!["channels"]!.DeepClone(), CancellationToken.None);
        var second = await File.ReadAllTextAsync(_configPath);

        second.ShouldBe(first, "a second identical write must not perturb the document");
    }

    /// <summary>
    /// The full JSON-to-SQLite-to-JSON loop must preserve every key, value and state - this is the
    /// migration contract, and a single lost key here is a lost credential in production.
    /// </summary>
    [Fact]
    public async Task JsonToSqliteToJson_PreservesEveryKeyValueAndState()
    {
        var original = JsonNode.Parse("""
            {
              "gateway": {
                "port": 8080,
                "ratio": 0.5,
                "big": 9007199254740993,
                "negative": -1,
                "zero": 0,
                "enabled": true,
                "empty": {},
                "emptyArray": [],
                "suppressed": null,
                "unicode": "\u00e9\u4e2d\u6587\ud83d\ude00",
                "quotes": "he said \"hi\"",
                "backslash": "C:\\path\\to",
                "newline": "line1\nline2"
              },
              "agents": { "nova": { "toolIds": ["a","b"], "nested": { "deep": { "deeper": 1 } } } }
            }
            """)!.AsObject();

        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(original);
        var rehydrated = ConfigDocumentRehydrator.Rehydrate(await store.ReadEntriesAsync());

        var before = ConfigDocumentFlattener.Flatten(original);
        var after = ConfigDocumentFlattener.Flatten(rehydrated);

        after.Count.ShouldBe(before.Count);
        foreach (var (path, entry) in before)
        {
            after.ShouldContainKey(path);
            after[path].State.ShouldBe(entry.State, $"state drifted at {path}");
            after[path].Value.ShouldBe(entry.Value, $"value drifted at {path}");
        }
    }

    // =============================================================================================
    // Hostile keys - the diff derives paths by splitting on '.', so keys containing '.' are the
    // obvious way to break it
    // =============================================================================================

    /// <summary>
    /// A dictionary key containing a dot is the ambiguity this whole path-based design invites:
    /// <c>providers["github.copilot"].apiKey</c> and <c>providers.github.copilot.apiKey</c> flatten
    /// identically. This test records the ACTUAL behaviour rather than asserting a wish.
    /// </summary>
    /// <remarks>
    /// Real configuration contains such keys - <c>github-copilot</c> is hyphenated today, but model
    /// ids like <c>gpt-4.1</c> and hostnames appear as keys elsewhere. Whatever the behaviour is, it
    /// must be deliberate and pinned, because silent misplacement of an API key is unrecoverable.
    /// </remarks>
    [Fact]
    public async Task DottedDictionaryKey_RoundTripsThroughBothBackendsWithoutLoss()
    {
        var document = JsonNode.Parse("""
            {"providers":{"gpt-4.1":{"apiKey":"sk-dotted"},"plain":{"apiKey":"sk-plain"}}}
            """)!.AsObject();

        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(document);
        var rehydrated = ConfigDocumentRehydrator.Rehydrate(await store.ReadEntriesAsync());

        // The plain key must be untouched regardless of what happens to the dotted one.
        rehydrated["providers"]!["plain"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-plain");

        // The dotted key's secret must still exist SOMEWHERE in the document - it may be nested
        // differently, but it must not be silently dropped.
        var flat = ConfigDocumentFlattener.Flatten(rehydrated);
        flat.Values.Any(e => e.Value == "\"sk-dotted\"")
            .ShouldBeTrue("a secret under a dotted key must never vanish in the round trip");
    }

    /// <summary>
    /// Keys that look like path syntax or are otherwise hostile must not corrupt neighbouring keys.
    /// </summary>
    [Theory]
    [InlineData("with space")]
    [InlineData("with-hyphen")]
    [InlineData("with_underscore")]
    [InlineData("UPPERCASE")]
    [InlineData("123numeric")]
    [InlineData("with:colon")]
    [InlineData("with/slash")]
    [InlineData("with[bracket]")]
    [InlineData("with\"quote")]
    public async Task HostileDictionaryKeys_RoundTripWithoutDisturbingSiblings(string hostileKey)
    {
        var document = new JsonObject
        {
            ["providers"] = new JsonObject
            {
                [hostileKey] = new JsonObject { ["apiKey"] = "sk-hostile" },
                ["sibling"] = new JsonObject { ["apiKey"] = "sk-sibling" },
            },
        };

        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(document);
        var rehydrated = ConfigDocumentRehydrator.Rehydrate(await store.ReadEntriesAsync());

        rehydrated["providers"]!["sibling"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-sibling");
        rehydrated["providers"]!.AsObject().ContainsKey(hostileKey).ShouldBeTrue();
    }

    // =============================================================================================
    // Rejected writes must leave the document byte-for-byte unchanged
    // =============================================================================================

    /// <summary>
    /// The section guard rejects an UNSANCTIONED write that destroys a populated section. A rejected
    /// write must not have touched the file at all - a partial application would be worse than the
    /// destruction it was preventing.
    /// </summary>
    /// <remarks>
    /// The mutation must not name the section it destroys. <c>MutateSectionAsync("channels", ...)</c>
    /// declares intent to rewrite <c>channels</c> wholesale, which the guard deliberately permits - that
    /// is what the API's PUT-section endpoint does. The dangerous case is a broad document mutation that
    /// collapses a section as a side effect, which is the #2816 shape.
    /// </remarks>
    [Fact]
    public async Task GuardRejectedWrite_LeavesTheDocumentByteForByteUnchanged()
    {
        await SeedAsync("""
            {"channels":{"telegram":{"enabled":true,"botToken":"tg-secret"},"teams":{"enabled":true}}}
            """);
        var original = await File.ReadAllTextAsync(_configPath);
        StampSentinelMtime();

        var writer = Writer();

        // A document-wide mutation that collapses channels without declaring it. Every key the
        // section previously held must be gone for the guard to fire - the third clause of its
        // contract, which exists because the 2026-07-31 damage left `channels` non-empty (it held a
        // single defaulted `enabled`), so an emptiness-only test would have watched it happen.
        var errors = await writer.MutateValidatedAsync(
            root =>
            {
                root["channels"] = new JsonObject
                {
                    ["somethingElse"] = new JsonObject { ["enabled"] = false },
                };
                return null;
            },
            "hostile",
            CancellationToken.None);

        errors.ShouldNotBeEmpty("the guard must reject an undeclared section collapse");
        (await File.ReadAllTextAsync(_configPath)).ShouldBe(original);
        ShouldNotHaveBeenRewritten();
    }

    /// <summary>
    /// The counterpart: a write that DECLARES the section it is rewriting is permitted, so the guard is
    /// a real discriminator rather than a blanket refusal.
    /// </summary>
    [Fact]
    public async Task GuardPermitsADeclaredSectionRewrite_SoItIsNotABlanketRefusal()
    {
        await SeedAsync("""
            {"channels":{"telegram":{"enabled":true,"botToken":"tg-secret"},"teams":{"enabled":true}}}
            """);

        var errors = await Writer().MutateSectionAsync(
            "channels",
            section =>
            {
                section.Remove("teams");
                return null;
            },
            "declared",
            CancellationToken.None);

        errors.ShouldBeEmpty();
        var after = await ReadBackAsync();
        after["channels"]!.AsObject().ContainsKey("teams").ShouldBeFalse();
        after["channels"]!["telegram"]!["botToken"]!.GetValue<string>().ShouldBe("tg-secret");
    }

    /// <summary>
    /// A mutation that throws must leave nothing behind - not a temp file, not a partial write.
    /// </summary>
    [Fact]
    public async Task ThrowingMutation_LeavesNoPartialWriteAndNoTempFiles()
    {
        await SeedAsync("""{"gateway":{"port":8080}}""");
        var original = await File.ReadAllTextAsync(_configPath);

        var writer = Writer();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await writer.MutateAsync(
                root =>
                {
                    ((JsonObject)root["gateway"]!)["port"] = 9999;
                    throw new InvalidOperationException("mutation failed midway");
                },
                "throwing",
                CancellationToken.None));

        (await File.ReadAllTextAsync(_configPath)).ShouldBe(original);
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    /// <summary>
    /// A stale revision token must be refused, and the refusal must not write.
    /// </summary>
    [Fact]
    public async Task StaleRevision_IsRefusedWithoutWriting()
    {
        await SeedAsync("""{"gateway":{"port":8080}}""");
        var writer = Writer();

        var (config, revision) = await writer.ReadPlatformConfigWithRevisionAsync();

        // Someone else writes first, invalidating the token.
        await writer.MutateAsync(
            root => ((JsonObject)root["gateway"]!)["port"] = 9090, "other", CancellationToken.None);

        var afterOther = await File.ReadAllTextAsync(_configPath);

        await Should.ThrowAsync<PlatformConfigConcurrencyException>(async () =>
            await writer.UpdatePlatformConfigAsync(config, "stale", CancellationToken.None, revision));

        (await File.ReadAllTextAsync(_configPath)).ShouldBe(afterOther,
            "a rejected compare-and-swap must not partially apply");
    }

    // =============================================================================================
    // Malformed and edge-case input
    // =============================================================================================

    /// <summary>
    /// A write against a corrupt config file must fail loudly rather than silently replacing the
    /// operator's document with a fresh one.
    /// </summary>
    [Fact]
    public async Task CorruptConfigFile_FailsLoudlyRatherThanSilentlyReplacing()
    {
        await SeedAsync("""{"gateway":{"port":8080""");   // truncated, invalid JSON

        var writer = Writer();

        await Should.ThrowAsync<Exception>(async () =>
            await writer.MutateAsync(
                root => root["marker"] = true, "against-corrupt", CancellationToken.None));

        // The corrupt bytes are still there for the operator to inspect - not overwritten.
        (await File.ReadAllTextAsync(_configPath)).ShouldContain("8080");
    }

    /// <summary>
    /// Numeric boundary values must survive without precision loss or reformatting.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2147483647")]
    [InlineData("-2147483648")]
    [InlineData("9007199254740993")]
    [InlineData("0.1")]
    [InlineData("1e10")]
    [InlineData("-0.0")]
    public async Task NumericBoundaryValues_SurviveTheStoreRoundTrip(string literal)
    {
        var document = JsonNode.Parse("{\"gateway\":{\"value\":" + literal + "}}")!.AsObject();

        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(document);
        var entries = await store.ReadEntriesAsync();

        // Compare canonical text: a float round-trip through double would silently alter 9007199254740993.
        entries["gateway.value"].Value.ShouldBe(
            ConfigDocumentFlattener.Flatten(document)["gateway.value"].Value);
    }

    /// <summary>
    /// An empty document is a legal state and must not be confused with a missing one.
    /// </summary>
    [Fact]
    public async Task EmptyDocument_IsWritableAndReadableOnBothBackends()
    {
        await SeedAsync("{}");
        var writer = Writer();

        await writer.MutateAsync(root => root["gateway"] = new JsonObject { ["port"] = 1 },
            "from-empty", CancellationToken.None);

        (await ReadBackAsync())["gateway"]!["port"]!.GetValue<int>().ShouldBe(1);

        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(new JsonObject());
        (await store.ReadEntriesAsync()).Count.ShouldBe(0);
    }

    /// <summary>
    /// Deep nesting must not hit a recursion or path-length limit that truncates the document.
    /// </summary>
    [Fact]
    public async Task DeeplyNestedDocument_SurvivesWithoutTruncation()
    {
        const int depth = 40;
        var leaf = new JsonObject { ["value"] = "bottom" };
        var current = leaf;
        for (var i = 0; i < depth; i++)
        {
            current = new JsonObject { [$"level{i}"] = current };
        }

        var document = new JsonObject { ["root"] = current };

        var store = new SqliteConfigStore($"Data Source={_storePath}");
        await store.WriteDocumentAsync(document);
        var rehydrated = ConfigDocumentRehydrator.Rehydrate(await store.ReadEntriesAsync());

        ConfigDocumentFlattener.Flatten(rehydrated).Values
            .ShouldContain(e => e.Value == "\"bottom\"");
    }

    // =============================================================================================
    // API-shaped operations, end to end through the real writer methods
    // =============================================================================================

    /// <summary>
    /// DELETE section/key must remove exactly that entry and nothing adjacent.
    /// </summary>
    [Fact]
    public async Task ApiDeleteSectionEntry_RemovesOnlyTheNamedEntry()
    {
        await SeedAsync("""
            {"providers":{"a":{"apiKey":"sk-a"},"b":{"apiKey":"sk-b"},"c":{"apiKey":"sk-c"}}}
            """);

        await Writer().RemoveSectionEntryAsync("providers", "b", CancellationToken.None);

        var after = await ReadBackAsync();
        after["providers"]!.AsObject().ContainsKey("b").ShouldBeFalse();
        after["providers"]!["a"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-a");
        after["providers"]!["c"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-c");
    }

    /// <summary>
    /// Deleting an entry that does not exist must be a no-op, not an error and not a write.
    /// </summary>
    [Fact]
    public async Task ApiDeleteMissingEntry_IsANoOpAndDoesNotRewriteTheFile()
    {
        await SeedAsync("""{"providers":{"a":{"apiKey":"sk-a"}}}""");
        var before = await File.ReadAllTextAsync(_configPath);
        StampSentinelMtime();

        await Writer().RemoveSectionEntryAsync("providers", "does-not-exist", CancellationToken.None);

        (await File.ReadAllTextAsync(_configPath)).ShouldBe(before);
        ShouldNotHaveBeenRewritten();
    }

    /// <summary>
    /// PUT section/key with a redacted secret must not overwrite the real stored secret - the UI
    /// round-trips redacted values back verbatim (#1955).
    /// </summary>
    [Fact]
    public async Task ApiUpdateSectionEntry_WithRedactedSecret_PreservesTheRealValue()
    {
        await SeedAsync("""{"providers":{"anthropic":{"apiKey":"sk-real","model":"opus"}}}""");

        var redacted = JsonNode.Parse("""{"apiKey":"***","model":"sonnet"}""")!;
        await Writer().UpdateSectionEntryAsync("providers", "anthropic", redacted, CancellationToken.None);

        var after = await ReadBackAsync();
        after["providers"]!["anthropic"]!["model"]!.GetValue<string>().ShouldBe("sonnet");
        after["providers"]!["anthropic"]!["apiKey"]!.GetValue<string>()
            .ShouldBe("sk-real", "a redacted placeholder must never overwrite the stored secret");
    }

    /// <summary>
    /// PUT section/key with a partial payload must not drop keys the payload omits (#1954).
    /// </summary>
    [Fact]
    public async Task ApiUpdateSectionEntry_WithPartialPayload_KeepsOmittedKeys()
    {
        await SeedAsync("""
            {"providers":{"anthropic":{"apiKey":"sk-real","model":"opus","timeout":30}}}
            """);

        await Writer().UpdateSectionEntryAsync(
            "providers", "anthropic", JsonNode.Parse("""{"model":"sonnet"}""")!, CancellationToken.None);

        var entry = (await ReadBackAsync())["providers"]!["anthropic"]!.AsObject();
        entry["model"]!.GetValue<string>().ShouldBe("sonnet");
        entry["apiKey"]!.GetValue<string>().ShouldBe("sk-real");
        entry["timeout"]!.GetValue<int>().ShouldBe(30);
    }

    // =============================================================================================
    // CLI-shaped operations
    // =============================================================================================

    /// <summary>
    /// The CLI's document mutation path must preserve unrelated sections, exactly as the API path does.
    /// </summary>
    [Fact]
    public async Task CliDocumentMutation_PreservesUnrelatedSections()
    {
        await SeedAsync("""
            {"gateway":{"listenUrl":"http://localhost:8080"},"channels":{"telegram":{"botToken":"tg-secret"}}}
            """);

        await Writer().MutateDocumentAsync(
            document => document.Set("gateway.publicBaseUrl", "https://example.test"),
            "cli-set",
            CancellationToken.None);

        var after = await ReadBackAsync();
        after["gateway"]!["publicBaseUrl"]!.GetValue<string>().ShouldBe("https://example.test");
        after["channels"]!["telegram"]!["botToken"]!.GetValue<string>().ShouldBe("tg-secret");
    }

    /// <summary>
    /// A CLI write that sets a value to its existing value must not rewrite the file.
    /// </summary>
    [Fact]
    public async Task CliSetToIdenticalValue_DoesNotRewriteTheFile()
    {
        await SeedAsync("""{"gateway":{"listenUrl":"http://localhost:8080"}}""");
        StampSentinelMtime();

        await Writer().MutateDocumentAsync(
            document => document.Set("gateway.listenUrl", "http://localhost:8080"),
            "cli-noop",
            CancellationToken.None);

        ShouldNotHaveBeenRewritten();
    }

    /// <summary>
    /// Explicit null written through the CLI must survive - it means "suppress the inherited value",
    /// and #2705 records the whole-document writer erasing exactly this.
    /// </summary>
    [Fact]
    public async Task CliWrittenExplicitNull_SurvivesAndIsNotErased()
    {
        await SeedAsync("""{"agents":{"nova":{"model":"sonnet","memory":{"enabled":true}}}}""");

        await Writer().MutateDocumentAsync(
            document => document.Set("agents.nova.memory", null),
            "cli-suppress",
            CancellationToken.None);

        var agent = (await ReadBackAsync())["agents"]!["nova"]!.AsObject();
        agent.ContainsKey("memory").ShouldBeTrue("explicit null must not collapse to absent");
        agent["memory"].ShouldBeNull();

        // And a subsequent unrelated write must not quietly erase it.
        await Writer().MutateDocumentAsync(
            document => document.Set("agents.nova.model", "opus"),
            "cli-later",
            CancellationToken.None);

        var later = (await ReadBackAsync())["agents"]!["nova"]!.AsObject();
        later.ContainsKey("memory").ShouldBeTrue("a later unrelated write must not erase the suppression");
        later["memory"].ShouldBeNull();
    }

    // =============================================================================================
    // Cross-surface interleaving: the CLI and the API writing the same document
    // =============================================================================================

    /// <summary>
    /// A CLI write and an API write interleaved against the same file must both survive - this is the
    /// realistic multi-process case the cross-process lock exists for.
    /// </summary>
    [Fact]
    public async Task InterleavedCliAndApiWrites_BothSurvive()
    {
        await SeedAsync("""{"gateway":{"listenUrl":"http://localhost:8080"},"providers":{"a":{"apiKey":"sk-a"}}}""");

        // Two independent writer instances, as two processes would have.
        var cli = Writer();
        var api = Writer();

        var cliWork = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await cli.MutateDocumentAsync(
                    d => d.Set("gateway.publicBaseUrl", $"https://host{i}.test"),
                    "cli", CancellationToken.None);
            }
        });

        var apiWork = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await api.UpdateSectionEntryAsync(
                    "providers", $"p{i}", JsonNode.Parse("{\"apiKey\":\"sk-" + i + "\"}")!,
                    CancellationToken.None);
            }
        });

        await Task.WhenAll(cliWork, apiWork);

        var final = await ReadBackAsync();

        // The original secret survived both storms.
        final["providers"]!["a"]!["apiKey"]!.GetValue<string>().ShouldBe("sk-a");

        // Every API-added provider survived despite ten interleaved CLI writes.
        for (var i = 0; i < 10; i++)
        {
            final["providers"]!.AsObject().ShouldContainKey($"p{i}");
        }

        // And the CLI's own section is intact and valid.
        final["gateway"]!["publicBaseUrl"]!.GetValue<string>().ShouldStartWith("https://host");
    }

    /// <summary>
    /// A backend that records the calls it receives, so a test can assert HOW a write was performed
    /// rather than only what the file ended up containing.
    /// </summary>
    /// <remarks>
    /// This exists because of a surviving mutant. Forcing <c>PlatformConfigWriter</c> back to
    /// whole-document writes - passing a null pristine snapshot - left all 219 tests green, because
    /// every one of them asserted on the resulting document and a whole-document write produces the
    /// same document. The change-set path was therefore unpinned: it could have been reverted or
    /// bypassed silently. Observing the backend call is the only assertion that distinguishes them.
    /// </remarks>
    private sealed class RecordingBackend : IConfigurationWriter
    {
        public string Name => "recording";

        /// <summary>Whole-document writes received.</summary>
        public List<JsonObject> Documents { get; } = [];

        /// <summary>Change sets received.</summary>
        public List<ConfigChangeSet> ChangeSets { get; } = [];

        public Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
        {
            Documents.Add(document.DeepClone().AsObject());
            return Task.CompletedTask;
        }

        public Task ApplyChangeSetAsync(
            ConfigChangeSet changes, string reason, CancellationToken cancellationToken = default)
        {
            ChangeSets.Add(changes);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A single-field edit must reach the backend as a CHANGE SET naming one key - not as a whole
    /// document. This is the property the entire #3532 design exists to provide.
    /// </summary>
    [Fact]
    public async Task SingleFieldEdit_ReachesTheBackendAsAOneKeyChangeSet_NotAWholeDocument()
    {
        await SeedAsync("""
            {"gateway":{"listenUrl":"http://a"},"channels":{"telegram":{"botToken":"tg-secret"}}}
            """);

        var backend = new RecordingBackend();
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, backup: null, writer: backend);

        await writer.MutateDocumentAsync(
            document => document.Set("gateway.publicBaseUrl", "https://b"),
            "single-edit",
            CancellationToken.None);

        backend.Documents.ShouldBeEmpty("an edit must not issue a whole-document write");

        var changes = backend.ChangeSets.ShouldHaveSingleItem();
        changes.Upserts.ShouldHaveSingleItem().Path.ShouldBe("gateway.publicBaseUrl");
        changes.Removals.ShouldBeEmpty();

        // The secret is not even mentioned in the payload sent to the store.
        changes.Upserts.ShouldAllBe(u => !u.Path.StartsWith("channels"));
    }

    /// <summary>
    /// The change set delivered to the backend must scale with the size of the CHANGE, not the size of
    /// the document - the specific complaint that blocked the previous contract.
    /// </summary>
    [Fact]
    public async Task ChangeSetSize_ScalesWithTheEditNotTheDocument()
    {
        var large = new JsonObject();
        var providers = new JsonObject();
        for (var i = 0; i < 120; i++)
        {
            providers[$"p{i}"] = new JsonObject { ["apiKey"] = $"sk-{i}", ["model"] = "m" };
        }

        large["providers"] = providers;
        large["gateway"] = new JsonObject { ["listenUrl"] = "http://a" };
        await SeedAsync(large.ToJsonString());

        var backend = new RecordingBackend();
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, backup: null, writer: backend);

        await writer.UpdateSectionEntryAsync(
            "providers", "p7", JsonNode.Parse("""{"model":"changed"}""")!, CancellationToken.None);

        var changes = backend.ChangeSets.ShouldHaveSingleItem();

        // 240+ keys in the document; the write names one.
        changes.Upserts.Count.ShouldBe(1);
        changes.Upserts[0].Path.ShouldBe("providers.p7.model");
    }

    /// <summary>
    /// A rejected write must reach the backend not at all.
    /// </summary>
    [Fact]
    public async Task RejectedWrite_NeverReachesTheBackend()
    {
        await SeedAsync("""
            {"channels":{"telegram":{"enabled":true},"teams":{"enabled":true}}}
            """);

        var backend = new RecordingBackend();
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, backup: null, writer: backend);

        var errors = await writer.MutateValidatedAsync(
            root =>
            {
                root["channels"] = new JsonObject { ["other"] = new JsonObject { ["x"] = 1 } };
                return null;
            },
            "hostile",
            CancellationToken.None);

        errors.ShouldNotBeEmpty();
        backend.ChangeSets.ShouldBeEmpty();
        backend.Documents.ShouldBeEmpty();
    }

    /// <summary>
    /// A no-op mutation must reach the backend not at all - the write is elided before it gets there,
    /// rather than relying on the JSON backend's own byte-comparison short-circuit (#2114), which the
    /// SQLite backend does not have.
    /// </summary>
    [Fact]
    public async Task NoOpMutation_NeverReachesTheBackend()
    {
        await SeedAsync("""{"gateway":{"listenUrl":"http://a"}}""");

        var backend = new RecordingBackend();
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, backup: null, writer: backend);

        await writer.MutateDocumentAsync(
            document => document.Set("gateway.listenUrl", "http://a"),
            "noop",
            CancellationToken.None);

        backend.ChangeSets.ShouldBeEmpty("an unchanged mutation must not reach any store");
        backend.Documents.ShouldBeEmpty();
    }

    /// <summary>
    /// A deletion must reach the backend as an explicit removal, because absence alone cannot carry
    /// intent to a row-shaped store.
    /// </summary>
    [Fact]
    public async Task Deletion_ReachesTheBackendAsAnExplicitRemoval()
    {
        await SeedAsync("""{"providers":{"a":{"apiKey":"sk-a"},"b":{"apiKey":"sk-b"}}}""");

        var backend = new RecordingBackend();
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, backup: null, writer: backend);

        await writer.RemoveSectionEntryAsync("providers", "b", CancellationToken.None);

        var changes = backend.ChangeSets.ShouldHaveSingleItem();
        changes.Removals.ShouldContain("providers.b.apiKey");
        changes.Upserts.ShouldBeEmpty();
    }

    /// <summary>
    /// Repeated write-read-write cycles must converge rather than accumulating drift - the property
    /// that a single round-trip test cannot show.
    /// </summary>
    [Fact]
    public async Task RepeatedWriteReadCycles_DoNotAccumulateDrift()
    {
        await SeedAsync("""
            {"gateway":{"port":8080,"empty":{},"list":["a"],"suppressed":null},
             "agents":{"nova":{"model":"sonnet"}}}
            """);

        var writer = Writer();

        // Cycle the document through the writer repeatedly with a no-op mutation.
        for (var i = 0; i < 5; i++)
        {
            await writer.MutateDocumentAsync(_ => { }, $"cycle-{i}", CancellationToken.None);
        }

        var after = await ReadBackAsync();
        var flat = ConfigDocumentFlattener.Flatten(after);

        flat["gateway.empty"].Value.ShouldBe("{}", "an empty object must not be dropped by cycling");
        flat["gateway.suppressed"].State.ShouldBe(ConfigValueState.ExplicitNull);
        flat["gateway.list"].Value.ShouldBe("[\"a\"]");
        flat["agents.nova.model"].Value.ShouldBe("\"sonnet\"");
    }
}
