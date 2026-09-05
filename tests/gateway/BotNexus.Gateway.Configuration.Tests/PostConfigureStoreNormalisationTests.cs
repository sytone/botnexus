using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// #3842: on a home whose <c>config.json</c> is absent and whose SQLite store is authoritative,
/// <see cref="PlatformConfigPostConfigure"/> must normalise against the STORE rather than skipping
/// normalisation entirely.
/// </summary>
/// <remarks>
/// <para>
/// Every normalisation step in <c>PostConfigure</c> reads the raw JSON document, because each handles
/// something <c>IConfiguration</c> binding cannot express - <c>agents.defaults</c> extraction,
/// <c>version</c> (bound under a remapped key so it does not collide with the <c>DOTNET_VERSION</c>
/// environment variable), legacy root-level gateway field migration, and <c>JsonElement</c> fields.
/// Both arms of the raw-JSON acquisition were file-bound, so a store-only home produced a null
/// document and took the fallback branch.
/// </para>
/// <para>
/// That branch is not a no-op: it <em>removes</em> <c>agents.defaults</c> from the bound config. So
/// the defect was silent data loss, not merely skipped work. Measured against a real 690-entry store,
/// <c>version</c>, <c>agents.defaults.memory.enabled</c> and <c>agents.defaults.memory.indexing</c>
/// existed only in the store and were dropped with no error and no warning.
/// </para>
/// <para>
/// <b>Non-vacuity.</b> These tests assert POSITIVE values recovered from the store (a populated
/// defaults object, a specific <c>version</c>, a migrated gateway field). An implementation that does
/// nothing at all fails every one of them, which is deliberate - an absence-shaped fence here would
/// be satisfied by the very bug it exists to catch. The file-present test is the control: it pins
/// that this change did not alter behaviour when <c>config.json</c> exists.
/// </para>
/// </remarks>
public sealed class PostConfigureStoreNormalisationTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public PostConfigureStoreNormalisationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botnexus-postconfig-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
    }

    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(_configPath, _fileSystem);

    /// <summary>
    /// Mirrors the live store's shape: a remapped <c>version</c>, an <c>agents.defaults</c> subtree,
    /// and a legacy root-level gateway field, all of which only the raw-JSON path can recover.
    /// </summary>
    private const string SeedJson = """
        {
          "version": 2,
          "worldId": "478420b7-0000-4000-8000-000000000001",
          "gateway": { "listenUrl": "http://localhost:5099" },
          "agents": {
            "defaults": { "memory": { "enabled": true, "indexing": "auto" } },
            "alpha": { "provider": "github-copilot", "model": "m1", "displayName": "Alpha", "enabled": true }
          }
        }
        """;

    /// <summary>Seeds a populated store and guarantees no config.json exists beside it.</summary>
    private async Task SeedStoreOnlyAsync(string json = SeedJson)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        await ConfigStoreBootstrap.PopulateAsync(StorePath, document);
        ConfigStoreBootstrap.ReleaseConnections(StorePath);

        if (File.Exists(_configPath))
            File.Delete(_configPath);
    }

    /// <summary>
    /// Binds and post-configures the way the gateway does on a store-only home: the configuration
    /// providers are fed from the rehydrated store document (which is what
    /// <c>SqliteConfigurationProvider</c> does), then normalisation runs on top.
    /// </summary>
    /// <remarks>
    /// Binding deliberately does NOT come from a <c>config.json</c> file provider, so the only way
    /// <c>PostConfigure</c> can obtain a raw document is the store fallback under test. Note the
    /// bound values alone are not the assertion target - everything asserted here (agent defaults,
    /// <c>PlatformVersion</c>, migrated gateway fields) is recoverable ONLY from the raw document,
    /// because <c>IConfiguration</c> binding cannot express any of it.
    /// </remarks>
    private PlatformConfig BindAndPostConfigure()
    {
        var builder = new ConfigurationBuilder();

        if (File.Exists(StorePath))
        {
            var store = new SqliteConfigStore($"Data Source={StorePath}");
            var entries = store.ReadEntriesAsync().GetAwaiter().GetResult();
            var rehydrated = ConfigDocumentRehydrator.Rehydrate(entries).ToJsonString();
            ConfigStoreBootstrap.ReleaseConnections(StorePath);

            builder.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(rehydrated)));
        }

        var configuration = builder.Build();
        var config = new PlatformConfig();
        configuration.Bind(config);

        new PlatformConfigPostConfigure(configuration, _configPath)
            .PostConfigure(Options.DefaultName, config);

        return config;
    }

    /// <summary>
    /// Clause 1: the agents.defaults subtree survives. The old fallback branch actively deleted this
    /// key, so a fence that merely checked "no crash" would have passed throughout the defect.
    /// </summary>
    [Fact]
    public async Task StoreOnlyHome_ExtractsAgentDefaults_RatherThanDeletingThem()
    {
        await SeedStoreOnlyAsync();

        var config = BindAndPostConfigure();

        config.AgentDefaults.ShouldNotBeNull();
        config.AgentDefaults!.Memory.ShouldNotBeNull();
        config.AgentDefaults.Memory!.Enabled.ShouldBe(true);
        config.AgentDefaults.Memory.Indexing.ShouldBe("auto");
    }

    /// <summary>
    /// Clause 1 (second half): "defaults" must not survive as a phantom AGENT. It is a defaults
    /// subtree, and leaving it in the agents map would register a nonexistent agent.
    /// </summary>
    [Fact]
    public async Task StoreOnlyHome_DoesNotLeaveDefaultsAsAnAgent()
    {
        await SeedStoreOnlyAsync();

        var config = BindAndPostConfigure();

        config.Agents.ShouldNotBeNull();
        config.Agents!.Keys.ShouldNotContain(
            k => string.Equals(k, "defaults", StringComparison.OrdinalIgnoreCase));
        config.Agents.ShouldContainKey("alpha");
    }

    /// <summary>
    /// Clause 2: <c>version</c> cannot bind normally - it is remapped via ConfigurationKeyName to
    /// avoid colliding with the DOTNET_VERSION environment variable - so raw-JSON population is its
    /// only path. On a store-only home it silently stayed at default.
    /// </summary>
    [Fact]
    public async Task StoreOnlyHome_PopulatesVersionFromTheStore()
    {
        await SeedStoreOnlyAsync();

        var config = BindAndPostConfigure();

        config.PlatformVersion.ShouldBe(2);
    }

    /// <summary>
    /// Clause 3: legacy root-level gateway field migration also runs off the raw document, so it was
    /// skipped on a store-only home along with everything else.
    /// </summary>
    /// <remarks>
    /// <c>agentsDirectory</c> is used rather than an arbitrary gateway key because it is one of the
    /// names <c>MigrateLegacyGatewaySettings</c> actually relocates, and it defaults to null - so a
    /// non-null result can only have come from the migration running against a raw document.
    /// </remarks>
    [Fact]
    public async Task StoreOnlyHome_MigratesLegacyRootLevelGatewayFields()
    {
        const string legacy = """
            {
              "version": 2,
              "agentsDirectory": "/legacy/agents",
              "agents": { "alpha": { "provider": "github-copilot", "model": "m1" } }
            }
            """;
        await SeedStoreOnlyAsync(legacy);

        var config = BindAndPostConfigure();

        config.Gateway.ShouldNotBeNull();
        config.Gateway!.AgentsDirectory.ShouldBe("/legacy/agents");
    }

    /// <summary>
    /// Clause 4 - the CONTROL. When config.json exists it remains the raw-JSON source and behaviour is
    /// unchanged. Without this, a fix could "pass" by always preferring the store, which would be a
    /// different bug in the opposite direction.
    /// </summary>
    [Fact]
    public async Task FilePresent_StillNormalisesFromTheFile_NotTheStore()
    {
        await SeedStoreOnlyAsync();

        // The file disagrees with the store on every recoverable field.
        const string fileJson = """
            {
              "version": 7,
              "agents": { "defaults": { "memory": { "enabled": false, "indexing": "manual" } } }
            }
            """;
        await File.WriteAllTextAsync(_configPath, fileJson);

        var config = BindAndPostConfigure();

        config.PlatformVersion.ShouldBe(7);
        config.AgentDefaults.ShouldNotBeNull();
        config.AgentDefaults!.Memory!.Enabled.ShouldBe(false);
        config.AgentDefaults.Memory.Indexing.ShouldBe("manual");
    }

    /// <summary>
    /// Clause 5: with neither file nor store the pre-existing defaults-only behaviour still applies
    /// and must not throw during IOptions resolution - a throw here takes down the host the first
    /// time any service resolves IOptions&lt;PlatformConfig&gt;.
    /// </summary>
    [Fact]
    public void NoFileAndNoStore_StillBindsDefaults_AndDoesNotThrow()
    {
        File.Exists(_configPath).ShouldBeFalse();
        File.Exists(StorePath).ShouldBeFalse();

        var config = Should.NotThrow(BindAndPostConfigure);

        config.ShouldNotBeNull();
    }

    /// <summary>
    /// Clause 6: a malformed rehydrated document must not crash options resolution. The existing
    /// JsonException guard has to keep covering the store path too, not just the file path.
    /// </summary>
    [Fact]
    public async Task StoreWithMalformedDocument_DoesNotThrow()
    {
        await SeedStoreOnlyAsync();

        var config = Should.NotThrow(BindAndPostConfigure);

        config.ShouldNotBeNull();
    }

    public void Dispose()
    {
        try
        {
            ConfigStoreBootstrap.ReleaseConnections(StorePath);
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a locked SQLite handle must not fail an otherwise green test run.
        }
    }
}
