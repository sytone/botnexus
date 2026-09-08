using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// #3823 S2: a configuration write on an installation whose <c>config.json</c> is absent must diff
/// against the STORE, not against an empty document.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PlatformConfigWriter"/> reads a pristine document before every mutation and hands it to
/// <see cref="ConfigDocumentDiffer"/> as the <em>before</em> side. The previous implementation
/// returned <c>new JsonObject()</c> whenever the config file did not exist, which is correct only
/// while "no file" implies "no configuration".
/// </para>
/// <para>
/// Once <c>config.db</c> is authoritative that implication is false, and the consequence is not a
/// crash. The differ sees an empty before-document and a fully populated after-document, so it emits
/// every stored key as an upsert and no key as a removal - a one-field edit producing a change set
/// spanning the entire configuration, from a write that returns success. These tests assert the
/// change set is SMALL, because asserting only the final values would pass under exactly the bug
/// they exist to catch.
/// </para>
/// </remarks>
public sealed class WriterPristineDocumentFromStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public WriterPristineDocumentFromStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botnexus-pristine-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
    }

    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(_configPath, _fileSystem);

    private const string SeedJson = """
        {
          "version": 1,
          "gateway": { "listenUrl": "http://localhost:5000", "defaultTimezone": "America/Los_Angeles" },
          "channels": { "telegram": { "type": "telegram", "enabled": true, "botToken": "secret-token" } },
          "agents": {
            "alpha": { "provider": "github-copilot", "model": "m1", "displayName": "Alpha", "enabled": true },
            "beta":  { "provider": "github-copilot", "model": "m2", "displayName": "Beta",  "enabled": true }
          }
        }
        """;

    /// <summary>
    /// Seeds a populated store and removes config.json, which is the shape of a store-only install.
    /// </summary>
    private async Task<RecordingWriter> SeedStoreOnlyAsync()
    {
        var document = JsonNode.Parse(SeedJson)!.AsObject();
        await ConfigStoreBootstrap.PopulateAsync(StorePath, document);
        ConfigStoreBootstrap.ReleaseConnections(StorePath);

        if (File.Exists(_configPath))
            File.Delete(_configPath);

        return new RecordingWriter();
    }

    private PlatformConfigWriter CreateWriter(RecordingWriter recorder)
        => new(
            _configPath,
            _fileSystem,
            backup: null,
            writer: recorder,
            pristineStore: new SqliteConfigStore($"Data Source={StorePath}"));

    [Fact]
    public async Task WriteWithNoConfigFile_DiffsAgainstTheStore_NotAnEmptyDocument()
    {
        var recorder = await SeedStoreOnlyAsync();
        var writer = CreateWriter(recorder);

        await writer.MutateAsync(
            root => root["gateway"]!.AsObject()["listenUrl"] = "http://localhost:6000",
            "test-edit",
            CancellationToken.None);

        recorder.LastChangeSet.ShouldNotBeNull();

        var upserts = recorder.LastChangeSet!.Upserts.Select(u => u.Path).ToList();
        upserts.ShouldBe(["gateway.listenUrl"]);
        recorder.LastChangeSet.Removals.ShouldBeEmpty();
    }

    /// <summary>
    /// The mutation must see the STORE's values, not an empty document - otherwise a caller that
    /// reads-modifies a subtree operates on nothing and silently discards the rest.
    /// </summary>
    [Fact]
    public async Task WriteWithNoConfigFile_PresentsTheStoredDocumentToTheMutation()
    {
        var recorder = await SeedStoreOnlyAsync();
        var writer = CreateWriter(recorder);

        JsonObject? observed = null;
        await writer.MutateAsync(
            root =>
            {
                observed = (JsonObject)root.DeepClone();
                root["gateway"]!.AsObject()["listenUrl"] = "http://localhost:6000";
            },
            "test-edit",
            CancellationToken.None);

        observed.ShouldNotBeNull();
        observed!["agents"]!.AsObject().Count.ShouldBe(2);
        observed["channels"]!["telegram"]!["botToken"]!.GetValue<string>().ShouldBe("secret-token");
        observed["gateway"]!["defaultTimezone"]!.GetValue<string>().ShouldBe("America/Los_Angeles");
    }

    /// <summary>
    /// With no file AND no store there is genuinely no configuration, so an empty document is the
    /// correct pristine state. Pins the fallback so the fix does not overreach.
    /// </summary>
    [Fact]
    public async Task WriteWithNoFileAndNoStore_StillStartsFromAnEmptyDocument()
    {
        var recorder = new RecordingWriter();
        var writer = new PlatformConfigWriter(_configPath, _fileSystem, backup: null, writer: recorder);

        await writer.MutateAsync(
            root => root["gateway"] = new JsonObject { ["listenUrl"] = "http://localhost:6000" },
            "test-edit",
            CancellationToken.None);

        recorder.LastChangeSet.ShouldNotBeNull();
        recorder.LastChangeSet!.Upserts.Select(u => u.Path).ShouldBe(["gateway.listenUrl"]);
    }

    /// <summary>
    /// The file remains the source when it exists, so a store-backed install with both present is
    /// unchanged by this fix.
    /// </summary>
    [Fact]
    public async Task WriteWithConfigFilePresent_StillDiffsAgainstTheFile()
    {
        var document = JsonNode.Parse(SeedJson)!.AsObject();
        await ConfigStoreBootstrap.PopulateAsync(StorePath, document);
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        File.WriteAllText(_configPath, SeedJson);

        var recorder = new RecordingWriter();
        var writer = CreateWriter(recorder);

        await writer.MutateAsync(
            root => root["gateway"]!.AsObject()["listenUrl"] = "http://localhost:7000",
            "test-edit",
            CancellationToken.None);

        recorder.LastChangeSet.ShouldNotBeNull();
        recorder.LastChangeSet!.Upserts.Select(u => u.Path).ShouldBe(["gateway.listenUrl"]);
        recorder.LastChangeSet.Removals.ShouldBeEmpty();
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Captures the change set the writer hands to its backend. Asserting on the final document
    /// would pass under the bug; asserting on the change set is what makes these tests non-vacuous.
    /// </summary>
    private sealed class RecordingWriter : IConfigurationWriter
    {
        public ConfigChangeSet? LastChangeSet { get; private set; }

        public string Name => "recording";

        public Task WriteAsync(
            JsonObject document,
            string reason,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ApplyChangeSetAsync(
            ConfigChangeSet changes,
            string reason,
            CancellationToken cancellationToken = default)
        {
            LastChangeSet = changes;
            return Task.CompletedTask;
        }
    }
}
