using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// The SQLite configuration store can be created, is then actually read, and can be removed (#3514).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this suite exists.</b> Every piece of the store was individually correct and the
/// composition did nothing: <c>WriteDocumentAsync</c> had no production caller after the shadow
/// migration was deleted (#3510), so <c>config.db</c> never appeared, and provider registration is
/// gated on that file existing. The store was unreachable by any supported action, and no test
/// noticed because each component's own tests passed.
/// </para>
/// <para>
/// So these assert the WHOLE loop - create the store, then read configuration through the real
/// provider pipeline - rather than unit-testing the writer. A test that only proved
/// <c>WriteDocumentAsync</c> writes rows would have passed throughout the entire period the feature
/// was unusable.
/// </para>
/// </remarks>
public sealed class ConfigStoreBootstrapTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public ConfigStoreBootstrapTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botnexus-store-bootstrap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
    }

    private void WriteConfig(string json) => File.WriteAllText(_configPath, json);

    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(_configPath, _fileSystem);

    private IConfiguration BuildPipeline()
        => new ConfigurationBuilder()
            .AddPlatformConfiguration(_configPath, fileSystem: _fileSystem)
            .Build();

    /// <summary>
    /// The headline case: after enabling the store, configuration is actually served from it.
    /// </summary>
    [Fact]
    public async Task AfterPopulate_TheStoreServesConfiguration()
    {
        WriteConfig("""{ "gateway": { "listenUrl": "http://localhost:5000" } }""");

        // Before: file only.
        BuildPipeline()["gateway:listenUrl"].ShouldBe("http://localhost:5000");

        // Enable the store from the current document.
        var document = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        await ConfigStoreBootstrap.PopulateAsync(StorePath, document);

        File.Exists(StorePath).ShouldBeTrue("enabling the store must create config.db");

        // Now change ONLY the store, so a value served from the file is distinguishable from one
        // served by the store.
        await ConfigStoreBootstrap.PopulateAsync(
            StorePath,
            JsonNode.Parse("""{ "gateway": { "listenUrl": "http://localhost:9999" } }""")!.AsObject());

        BuildPipeline()["gateway:listenUrl"].ShouldBe(
            "http://localhost:9999",
            "the store must win over config.json once it exists");
    }

    /// <summary>
    /// Deleting the store returns the gateway to file-only configuration. The documented rollback
    /// must actually work.
    /// </summary>
    /// <remarks>
    /// The release-then-delete order is the fix for a real operator-facing defect, not a test
    /// convenience: Microsoft.Data.Sqlite pools connections by connection string, so a handle from an
    /// earlier read keeps <c>config.db</c> open and a bare delete fails with "the process cannot
    /// access the file". This test failed exactly that way before
    /// <see cref="ConfigStoreBootstrap.ReleaseConnections"/> existed - which is what an operator
    /// running <c>config store disable</c> after <c>config store status</c> would have seen.
    /// </remarks>
    [Fact]
    public async Task AfterDelete_ConfigurationReturnsToTheFile()
    {
        WriteConfig("""{ "gateway": { "listenUrl": "http://localhost:5000" } }""");

        await ConfigStoreBootstrap.PopulateAsync(
            StorePath,
            JsonNode.Parse("""{ "gateway": { "listenUrl": "http://localhost:9999" } }""")!.AsObject());

        BuildPipeline()["gateway:listenUrl"].ShouldBe("http://localhost:9999");

        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        File.Delete(StorePath);

        BuildPipeline()["gateway:listenUrl"].ShouldBe(
            "http://localhost:5000",
            "deleting config.db must return configuration to the file");
    }

    /// <summary>
    /// No store means byte-identical file-only behaviour - an installation that never opts in is
    /// unaffected.
    /// </summary>
    [Fact]
    public void WithNoStore_ConfigurationComesFromTheFile()
    {
        WriteConfig("""{ "gateway": { "listenUrl": "http://localhost:5000", "defaultAgentId": "alpha" } }""");

        File.Exists(StorePath).ShouldBeFalse();

        var config = BuildPipeline();
        config["gateway:listenUrl"].ShouldBe("http://localhost:5000");
        config["gateway:defaultAgentId"].ShouldBe("alpha");
    }

    /// <summary>
    /// An empty store contributes no keys, so the file continues to serve every value. This is what
    /// makes registering the provider safe rather than destructive.
    /// </summary>
    [Fact]
    public async Task WithEmptyStore_TheFileStillServesEveryValue()
    {
        WriteConfig("""{ "gateway": { "listenUrl": "http://localhost:5000" } }""");

        await ConfigStoreBootstrap.PopulateAsync(StorePath, new JsonObject());

        File.Exists(StorePath).ShouldBeTrue();
        BuildPipeline()["gateway:listenUrl"].ShouldBe(
            "http://localhost:5000",
            "an empty store must not erase configuration");
    }

    /// <summary>
    /// Populating preserves the tri-state the store exists to record: an explicit null is not the
    /// same as an absent key.
    /// </summary>
    [Fact]
    public async Task Populate_PreservesExplicitNull()
    {
        WriteConfig("""{ "agents": { "alpha": { "model": "x", "memory": null } } }""");

        var document = JsonNode.Parse(File.ReadAllText(_configPath))!.AsObject();
        await ConfigStoreBootstrap.PopulateAsync(StorePath, document);

        var store = new Store.SqliteConfigStore($"Data Source={StorePath}");
        var entries = await store.ReadEntriesAsync();

        entries.ShouldContainKey("agents.alpha.memory");
        entries["agents.alpha.memory"].State.ShouldBe(Store.ConfigValueState.ExplicitNull);
        entries.ContainsKey("agents.alpha.absent").ShouldBeFalse();
    }

    /// <summary>
    /// Entry count reports null when the store does not exist, and a real count when it does - so
    /// `config store status` can distinguish "not enabled" from "enabled but empty".
    /// </summary>
    [Fact]
    public async Task CountEntries_DistinguishesAbsentFromEmpty()
    {
        WriteConfig("""{ "gateway": { "listenUrl": "http://localhost:5000" } }""");

        (await ConfigStoreBootstrap.CountEntriesAsync(StorePath, _fileSystem))
            .ShouldBeNull("no store file means not enabled");

        await ConfigStoreBootstrap.PopulateAsync(StorePath, new JsonObject());
        (await ConfigStoreBootstrap.CountEntriesAsync(StorePath, _fileSystem))
            .ShouldBe(0, "an existing but empty store is a distinct state");

        await ConfigStoreBootstrap.PopulateAsync(
            StorePath,
            JsonNode.Parse("""{ "gateway": { "listenUrl": "x" } }""")!.AsObject());
        (await ConfigStoreBootstrap.CountEntriesAsync(StorePath, _fileSystem))
            .ShouldBe(1);
    }

    public void Dispose()
    {
        try
        {
            ConfigStoreBootstrap.ReleaseConnections(StorePath);
        }
        catch
        {
            // Best effort: the directory delete below is what matters.
        }

        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // SQLite file handles can linger briefly on Windows; cleanup is best effort.
        }
    }
}
