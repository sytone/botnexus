using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Shadow;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// Tests for the configuration store cutover (#3180).
///
/// <para>
/// <b>What is being proven.</b> That <c>ConfigStoreAuthoritative</c> actually changes which source the
/// platform loads from, and that every failure direction lands on the file rather than on a broken or
/// empty configuration. Before this change the flag was inert - registered, documented, and with no
/// effect - so the tests that matter are the ones that would still pass against an inert flag, and
/// therefore must be written to fail against one.
/// </para>
///
/// <para>
/// Runs against a real on-disk SQLite store rather than a fake. The failure being guarded against
/// lives in the storage and rehydration layer, and a fake would merely replay whatever belief was
/// encoded into it.
/// </para>
/// </summary>
public sealed class ConfigStoreCutoverTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"botnexus-config-cutover-{Guid.NewGuid():N}.db");

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

    private sealed class StubGate(bool authoritative) : IConfigStoreAuthoritativeGate
    {
        public Task<bool> IsAuthoritativeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(authoritative);
    }

    private sealed class ThrowingStore : IConfigStore
    {
        public Task<IReadOnlyDictionary<string, ConfigEntry>> ReadEntriesAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("store is broken");

        public Task WriteDocumentAsync(JsonObject document, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubFileSource(JsonObject? document) : IConfigShadowSource
    {
        public int ReadCount { get; private set; }

        public Task<JsonObject?> ReadRawDocumentAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(document);
        }
    }

    private sealed class CountingStore(IConfigStore inner) : IConfigStore
    {
        public int ReadCount { get; private set; }

        public Task<IReadOnlyDictionary<string, ConfigEntry>> ReadEntriesAsync(CancellationToken ct = default)
        {
            ReadCount++;
            return inner.ReadEntriesAsync(ct);
        }

        public Task WriteDocumentAsync(JsonObject document, CancellationToken ct = default)
            => inner.WriteDocumentAsync(document, ct);
    }

    private static StoreBackedConfigDocumentSource Source(
        IConfigStore store,
        IConfigShadowSource file,
        bool authoritative)
        => new(store, file, new StubGate(authoritative), NullLogger<StoreBackedConfigDocumentSource>.Instance);

    // ---------------------------------------------------------------------------------------------
    // AC1 - flag off means the store is never consulted
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Asserts the store is never even READ when the flag is off, rather than asserting the resulting
    /// document happens to match the file. A source that read the store and then discarded the result
    /// would satisfy the weaker assertion while still opening a database the operator never enabled.
    /// </summary>
    [Fact]
    public async Task FlagOff_DoesNotConsultTheStoreAtAll()
    {
        var store = new CountingStore(CreateStore());
        await store.WriteDocumentAsync(Obj("""{ "marker": "from-store" }"""));
        var countAfterSeed = store.ReadCount;

        var file = new StubFileSource(Obj("""{ "marker": "from-file" }"""));
        var read = await Source(store, file, authoritative: false).ReadAsync();

        read.Origin.ShouldBe(ConfigDocumentOrigin.File);
        read.FellBack.ShouldBeFalse();
        read.Document!["marker"]!.GetValue<string>().ShouldBe("from-file");
        store.ReadCount.ShouldBe(countAfterSeed);
    }

    // ---------------------------------------------------------------------------------------------
    // AC2 - flag on means the store actually serves the configuration
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The test an inert flag fails. The asserted value exists ONLY in the store, so a read that
    /// silently kept using the file cannot produce it.
    /// </summary>
    [Fact]
    public async Task FlagOn_ServesTheDocumentFromTheStore()
    {
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""{ "marker": "from-store", "gateway": { "listenUrl": "http://from-store:9999" } }"""));

        var file = new StubFileSource(Obj("""{ "marker": "from-file" }"""));
        var read = await Source(store, file, authoritative: true).ReadAsync();

        read.Origin.ShouldBe(ConfigDocumentOrigin.Store);
        read.FellBack.ShouldBeFalse();
        read.Document!["marker"]!.GetValue<string>().ShouldBe("from-store");
        read.Document["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://from-store:9999");
        file.ReadCount.ShouldBe(0);
    }

    /// <summary>
    /// End-to-end through the real loader: a store-only value must survive materialisation into a
    /// bound <see cref="PlatformConfig"/>, not merely appear in the raw document.
    /// </summary>
    [Fact]
    public async Task FlagOn_StoreValueReachesTheMaterialisedPlatformConfig()
    {
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""{ "gateway": { "listenUrl": "http://from-store:9911" } }"""));

        var file = new StubFileSource(Obj("""{ "gateway": { "listenUrl": "http://from-file:1234" } }"""));

        var config = await PlatformConfigLoader.LoadFromSourceAsync(
            Source(store, file, authoritative: true),
            validateOnLoad: false);

        config.Gateway!.ListenUrl.ShouldBe("http://from-store:9911");
    }

    // ---------------------------------------------------------------------------------------------
    // AC3/AC4 - every failure direction lands on the file, never on a broken configuration
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task FlagOn_StoreThrows_FallsBackToFileAndReportsIt()
    {
        var file = new StubFileSource(Obj("""{ "marker": "from-file" }"""));
        var read = await Source(new ThrowingStore(), file, authoritative: true).ReadAsync();

        read.Origin.ShouldBe(ConfigDocumentOrigin.File);
        read.FellBack.ShouldBeTrue();
        read.Document!["marker"]!.GetValue<string>().ShouldBe("from-file");
    }

    /// <summary>
    /// An empty store must not be served as an empty configuration. Doing so would silently reset
    /// every platform setting, which is far worse than ignoring the flag - and it is the more likely
    /// state, because it is what a store looks like before the shadow migration has ever run.
    /// </summary>
    [Fact]
    public async Task FlagOn_EmptyStore_FallsBackRatherThanServingAnEmptyConfiguration()
    {
        var file = new StubFileSource(Obj("""{ "marker": "from-file" }"""));
        var read = await Source(CreateStore(), file, authoritative: true).ReadAsync();

        read.Origin.ShouldBe(ConfigDocumentOrigin.File);
        read.FellBack.ShouldBeTrue();
        read.Document!["marker"]!.GetValue<string>().ShouldBe("from-file");
    }

    [Fact]
    public async Task NoDocumentAnywhere_YieldsDefaultConfigRatherThanThrowing()
    {
        var config = await PlatformConfigLoader.LoadFromSourceAsync(
            Source(CreateStore(), new StubFileSource(null), authoritative: true),
            validateOnLoad: false);

        config.ShouldNotBeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // AC5 - store-served documents get the identical FinishLoad pipeline
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A legacy-schema document served from the STORE must receive the same legacy migration a
    /// file-served one does. A cutover that bound the store document directly would skip it and
    /// produce a subtly different config from identical content.
    /// </summary>
    [Fact]
    public async Task StoreServedDocument_StillGetsAgentDefaultsExtraction()
    {
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""
            { "agents": { "defaults": { "toolTimeoutSeconds": 4242 } } }
            """));

        var config = await PlatformConfigLoader.LoadFromSourceAsync(
            Source(store, new StubFileSource(null), authoritative: true),
            validateOnLoad: false);

        config.AgentDefaults.ShouldNotBeNull();
        config.AgentDefaults!.ToolTimeoutSeconds.ShouldBe(4242);
    }

    // ---------------------------------------------------------------------------------------------
    // AC6 - tri-state survives the whole cutover path
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The distinction the entire migration exists to protect, asserted at the far end of the real
    /// path: store -> rehydrate -> document. Unset must be ABSENT and ExplicitNull must be
    /// PRESENT-and-null; collapsing either direction silently changes inheritance for every agent.
    /// </summary>
    [Fact]
    public async Task TriState_SurvivesTheCutoverPath()
    {
        var store = CreateStore();
        await store.WriteDocumentAsync(Obj("""{ "a": null, "b": "set" }"""));

        var read = await Source(store, new StubFileSource(null), authoritative: true).ReadAsync();

        read.Document!.ContainsKey("a").ShouldBeTrue();
        read.Document["a"].ShouldBeNull();
        read.Document["b"]!.GetValue<string>().ShouldBe("set");
        read.Document.ContainsKey("never-written").ShouldBeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // Startup gate - the flag read that happens before DI exists
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartupGate_ReadsTheFeatureManagementSection()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/cfg/config.json"] = new("""
                { "FeatureManagement": { "ConfigStoreAuthoritative": true } }
                """),
        });

        var gate = new StartupFlagAuthoritativeGate(fs, "/cfg/config.json");
        (await gate.IsAuthoritativeAsync()).ShouldBeTrue();
    }

    [Theory]
    [InlineData("""{ "FeatureManagement": { "ConfigStoreAuthoritative": false } }""")]
    [InlineData("""{ "FeatureManagement": { } }""")]
    [InlineData("""{ }""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public async Task StartupGate_FailsClosed(string content)
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            ["/cfg/config.json"] = new(content),
        });

        var gate = new StartupFlagAuthoritativeGate(fs, "/cfg/config.json");
        (await gate.IsAuthoritativeAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task StartupGate_MissingFile_FailsClosed()
    {
        var gate = new StartupFlagAuthoritativeGate(new MockFileSystem(), "/cfg/absent.json");
        (await gate.IsAuthoritativeAsync()).ShouldBeFalse();
    }
}
