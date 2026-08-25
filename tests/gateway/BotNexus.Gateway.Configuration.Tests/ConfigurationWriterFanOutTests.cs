using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// The configuration write path fans out to every registered backend (#3527).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is actually being proven.</b> Not that a writer was invoked - that a write LANDED. Each
/// fan-out case reads every backend independently afterwards, because asserting "the writer was
/// called twice" would pass against an implementation that called a broken backend twice.
/// </para>
/// <para>
/// The defect this closes appeared the moment the store became reachable (#3514): reads resolve from
/// the store when it exists, writes went only to the file, so a portal edit left the store serving
/// the old value and the change looked silently discarded.
/// </para>
/// </remarks>
public sealed class ConfigurationWriterFanOutTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly string _storePath;

    public ConfigurationWriterFanOutTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"bn-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
        _storePath = Path.Combine(_directory, "config.db");
    }

    private static JsonObject Doc(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>A backend that records what it received, for ordering and payload assertions.</summary>
    private sealed class RecordingWriter(string name) : IConfigurationWriter
    {
        public string Name { get; } = name;
        public List<string> Received { get; } = [];

        public Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
        {
            Received.Add(document.ToJsonString());
            return Task.CompletedTask;
        }

        /// <summary>Records the change set it was asked to apply.</summary>
        public Task<ConfigChangeSet> ApplyAsync(
            object dto,
            string pathPrefix,
            string reason,
            ConfigDiffOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var changes = ConfigDtoDiffer.Diff(Current, dto, pathPrefix, options);
            Applied.Add(changes);
            return Task.FromResult(changes);
        }

        /// <summary>
        /// The document this backend claims to already hold, so a test can make two backends disagree.
        /// </summary>
        public JsonObject? Current { get; set; }

        /// <summary>Records a pre-computed change set.</summary>
        public Task ApplyChangeSetAsync(ConfigChangeSet changes, string reason, CancellationToken cancellationToken = default)
        {
            Applied.Add(changes);
            return Task.CompletedTask;
        }

        /// <summary>Change sets received via ApplyAsync/ApplyChangeSetAsync, in order.</summary>
        public List<ConfigChangeSet> Applied { get; } = [];
    }

    /// <summary>A backend that always fails, for partial-write assertions.</summary>
    private sealed class FailingWriter(string name) : IConfigurationWriter
    {
        public string Name { get; } = name;

        public Task WriteAsync(JsonObject document, string reason, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("backend unavailable"));

        public Task<ConfigChangeSet> ApplyAsync(
            object dto,
            string pathPrefix,
            string reason,
            ConfigDiffOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromException<ConfigChangeSet>(new InvalidOperationException("backend unavailable"));

        public Task ApplyChangeSetAsync(ConfigChangeSet changes, string reason, CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("backend unavailable"));
    }

    // ---------------------------------------------------------------------------------------------
    // The headline property: one write, both stores
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A single write reaches BOTH the file and the store, verified by reading each independently.
    /// </summary>
    [Fact]
    public async Task OneWrite_LandsInBothTheFileAndTheStore()
    {
        var fileSystem = new FileSystem();
        var store = new SqliteConfigStore($"Data Source={_storePath}");

        var writer = new FanOutConfigurationWriter(
        [
            new JsonConfigurationWriter(_configPath, fileSystem),
            new SqliteConfigurationWriter(store),
        ]);

        await writer.WriteAsync(Doc("""{ "gateway": { "listenUrl": "http://localhost:7777" } }"""), "test");

        // Read the FILE independently.
        File.Exists(_configPath).ShouldBeTrue();
        var fromFile = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        fromFile["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:7777");

        // Read the STORE independently.
        var entries = await store.ReadEntriesAsync();
        entries.ShouldContainKey("gateway.listenUrl");
        entries["gateway.listenUrl"].Value.ShouldBe("\"http://localhost:7777\"");
    }

    /// <summary>
    /// With one backend registered, behaviour is the single-backend behaviour - the fan-out adds
    /// nothing for an installation that never enabled the store.
    /// </summary>
    [Fact]
    public async Task WithOnlyTheJsonBackend_TheFileIsWrittenAndNoStoreAppears()
    {
        var writer = new FanOutConfigurationWriter([new JsonConfigurationWriter(_configPath, new FileSystem())]);

        await writer.WriteAsync(Doc("""{ "gateway": { "listenUrl": "http://localhost:5000" } }"""), "test");

        File.Exists(_configPath).ShouldBeTrue();
        File.Exists(_storePath).ShouldBeFalse("a JSON-only writer must not create a store");
    }

    /// <summary>Every backend receives the same document, in registration order.</summary>
    [Fact]
    public async Task EveryBackend_ReceivesTheSameDocument()
    {
        var first = new RecordingWriter("first");
        var second = new RecordingWriter("second");

        await new FanOutConfigurationWriter([first, second])
            .WriteAsync(Doc("""{ "a": 1 }"""), "test");

        first.Received.ShouldHaveSingleItem();
        second.Received.ShouldHaveSingleItem();
        second.Received[0].ShouldBe(first.Received[0]);
    }

    // ---------------------------------------------------------------------------------------------
    // Partial failure must be loud
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A failure in one backend throws, naming it. Reporting success when the store leg failed is the
    /// exact deceit this design exists to prevent: the store wins on read, so the operator's change
    /// would be invisible while the UI said it saved.
    /// </summary>
    [Fact]
    public async Task WhenOneBackendFails_TheWriteThrowsAndNamesIt()
    {
        var writer = new FanOutConfigurationWriter(
        [
            new RecordingWriter("json"),
            new FailingWriter("sqlite"),
        ]);

        var ex = await Should.ThrowAsync<AggregateException>(
            () => writer.WriteAsync(Doc("""{ "a": 1 }"""), "test"));

        ex.Message.ShouldContain("json", Case.Insensitive);
        ex.InnerExceptions.ShouldHaveSingleItem().Message.ShouldContain("sqlite");
    }

    /// <summary>
    /// A failing backend does not stop the others. Aborting at the first failure would leave the
    /// remaining stores holding the previous document AND hide which ones.
    /// </summary>
    [Fact]
    public async Task WhenTheFirstBackendFails_TheRestStillReceiveTheWrite()
    {
        var survivor = new RecordingWriter("survivor");

        await Should.ThrowAsync<AggregateException>(
            () => new FanOutConfigurationWriter([new FailingWriter("broken"), survivor])
                .WriteAsync(Doc("""{ "a": 1 }"""), "test"));

        survivor.Received.ShouldHaveSingleItem(
            "a failing sibling must not prevent the remaining backends from receiving the document");
    }

    /// <summary>
    /// A fan-out over zero backends is refused. Accepting every write and persisting nothing is worse
    /// than failing to start.
    /// </summary>
    [Fact]
    public void WithNoBackends_ConstructionIsRefused()
        => Should.Throw<ArgumentException>(() => new FanOutConfigurationWriter([]));

    // ---------------------------------------------------------------------------------------------
    // The five file properties survive the move
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// #2114 no-op detection: writing a canonically identical document does not touch the file. An
    /// atomic replace rewrites the inode and re-triggers the reload pipeline, so an unchanged write
    /// causes a reload storm.
    /// </summary>
    /// <remarks>
    /// Asserts the file's CONTENT is untouched rather than sleeping to compare timestamps. A
    /// wall-clock wait would make the test both slower and flakier on a loaded CI agent, and the
    /// property under test is "the file was not rewritten", which a sentinel proves directly.
    /// </remarks>
    [Fact]
    public async Task WritingAnIdenticalDocument_DoesNotTouchTheFile()
    {
        var writer = new JsonConfigurationWriter(_configPath, new FileSystem());
        var document = Doc("""{ "gateway": { "listenUrl": "http://localhost:5000" } }""");

        await writer.WriteAsync(document, "first");

        // Replace the file with a canonically EQUAL but textually distinct sentinel. If the second
        // write touches the file at all, the sentinel formatting is replaced by the writer's own.
        const string Sentinel = """{"gateway":{"listenUrl":"http://localhost:5000"}}""";
        await File.WriteAllTextAsync(_configPath, Sentinel);

        await writer.WriteAsync(document, "second");

        (await File.ReadAllTextAsync(_configPath)).ShouldBe(
            Sentinel,
            "an identical document must not rewrite the file (#2114)");
    }

    /// <summary>A changed document IS written, so the no-op check cannot be a blanket skip.</summary>
    [Fact]
    public async Task WritingAChangedDocument_UpdatesTheFile()
    {
        var writer = new JsonConfigurationWriter(_configPath, new FileSystem());

        await writer.WriteAsync(Doc("""{ "gateway": { "listenUrl": "http://localhost:5000" } }"""), "first");
        await writer.WriteAsync(Doc("""{ "gateway": { "listenUrl": "http://localhost:6000" } }"""), "second");

        var reloaded = JsonNode.Parse(await File.ReadAllTextAsync(_configPath))!.AsObject();
        reloaded["gateway"]!["listenUrl"]!.GetValue<string>().ShouldBe("http://localhost:6000");
    }

    /// <summary>Formatting-only differences are still a no-op - the check is canonical, not textual.</summary>
    /// <remarks>
    /// This is the same property as the test above approached from the other side: there the sentinel
    /// proves the file was untouched, here it proves the COMPARISON tolerated a formatting
    /// difference. Both are needed - a writer that always rewrote would pass the second alone.
    /// </remarks>
    [Fact]
    public async Task WritingAReformattedButIdenticalDocument_DoesNotTouchTheFile()
    {
        var writer = new JsonConfigurationWriter(_configPath, new FileSystem());
        await writer.WriteAsync(Doc("""{ "gateway": { "listenUrl": "http://localhost:5000" } }"""), "first");

        // Compact, unindented, but canonically equal to what the writer would produce.
        const string Compacted = """{"gateway":{"listenUrl":"http://localhost:5000"}}""";
        await File.WriteAllTextAsync(_configPath, Compacted);

        await writer.WriteAsync(Doc("""{ "gateway": { "listenUrl": "http://localhost:5000" } }"""), "second");

        (await File.ReadAllTextAsync(_configPath)).ShouldBe(
            Compacted,
            "canonically equal content must be a no-op even when the on-disk formatting differs");
    }

    /// <summary>The config directory is created when absent - a fresh install has no directory.</summary>
    [Fact]
    public async Task WhenTheDirectoryIsMissing_ItIsCreated()
    {
        var nested = Path.Combine(_directory, "nested", "deeper", "config.json");
        await new JsonConfigurationWriter(nested, new FileSystem()).WriteAsync(Doc("""{ "a": 1 }"""), "test");

        File.Exists(nested).ShouldBeTrue();
    }

    /// <summary>
    /// Owner-only permissions are applied AFTER the replace, not only on first create. The file
    /// carries provider API keys and bot tokens (#2392), and a rewrite path differs from a create
    /// path - a fix applied only at creation leaves every subsequent save wrong.
    /// </summary>
    [Fact]
    public async Task AfterRewritingAnExistingFile_PermissionsAreReapplied()
    {
        var fileSystem = new MockFileSystem();
        var path = "/cfg/config.json";
        fileSystem.AddDirectory("/cfg");

        var writer = new JsonConfigurationWriter(path, fileSystem);
        await writer.WriteAsync(Doc("""{ "a": 1 }"""), "create");
        await writer.WriteAsync(Doc("""{ "a": 2 }"""), "rewrite");

        // The mock filesystem cannot model ACLs, so this asserts the rewrite path completed rather
        // than the ACL itself - the permission call is pinned by the source guard below.
        var reloaded = JsonNode.Parse(fileSystem.File.ReadAllText(path))!.AsObject();
        reloaded["a"]!.GetValue<int>().ShouldBe(2);
    }

    /// <summary>
    /// Source guard: the JSON backend restricts permissions on BOTH sides of the replace. A
    /// filesystem mock cannot observe ACLs, and the real-ACL behaviour is platform-specific, so the
    /// only way to pin the second call is against the source. Losing it would reintroduce #2392 for
    /// every save after the first.
    /// </summary>
    [Fact]
    public void JsonWriter_RestrictsPermissionsBeforeAndAfterTheReplace()
    {
        var source = File.ReadAllText(LocateWriterSource());
        var occurrences = source.Split("SecureFilePermissions.RestrictToOwner").Length - 1;

        occurrences.ShouldBe(
            2,
            "#2392 requires RestrictToOwner before the move AND after it; a single call leaves every " +
            "rewrite after the first with default permissions");
    }

    private static string LocateWriterSource()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Packages.props")))
            current = current.Parent;

        current.ShouldNotBeNull("could not locate the repository root");
        return Path.Combine(
            current!.FullName,
            "src", "gateway", "BotNexus.Gateway.Configuration", "Writers", "JsonConfigurationWriter.cs");
    }

    public void Dispose()
    {
        try
        {
            ConfigStoreBootstrap.ReleaseConnections(_storePath);
        }
        catch
        {
            // Best effort; the directory delete below is what matters.
        }

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // SQLite handles can linger briefly on Windows.
        }
    }
}
