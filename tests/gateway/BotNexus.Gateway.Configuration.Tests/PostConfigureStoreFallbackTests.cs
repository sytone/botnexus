using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration.Tests;

/// <summary>
/// #3842: <see cref="PlatformConfigPostConfigure"/> must normalise from the STORE when a home has no
/// <c>config.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both arms of the raw-JSON acquisition were file-bound, so on a store-only home the normalisation
/// input was null and the <c>else</c> branch ran. That branch does not merely skip work - it
/// <em>removes</em> the <c>defaults</c> key from <see cref="PlatformConfig.Agents"/>, so a store that
/// holds <c>agents.defaults</c> loses it, and <c>version</c>, the legacy gateway migration and every
/// <see cref="JsonElement"/> field are dropped with it. No error, no warning: the same silent-wrong-output
/// class as #3547, #3492 and #3824.
/// </para>
/// <para>
/// These tests assert against the store-only shape specifically. Asserting only that a file-backed home
/// still works would pass under exactly the bug they exist to catch.
/// </para>
/// </remarks>
public sealed class PostConfigureStoreFallbackTests : IDisposable
{
    private readonly string _directory;
    private readonly string _configPath;
    private readonly IFileSystem _fileSystem = new FileSystem();

    public PostConfigureStoreFallbackTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"botnexus-postconfigure-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configPath = Path.Combine(_directory, "config.json");
    }

    private string StorePath => ConfigStoreBootstrap.ResolveStorePath(_configPath, _fileSystem);

    /// <summary>
    /// Carries every element the normalisation steps are responsible for: <c>version</c> (which cannot
    /// bind normally), <c>agents.defaults</c>, a legacy root-level gateway field, and JsonElement-typed
    /// agent fields.
    /// </summary>
    private const string SeedJson = """
        {
          "version": 2,
          "listenUrl": "http://legacy-root:5099",
          "gateway": {
            "defaultTimezone": "America/Los_Angeles",
            "extensions": { "defaults": { "sample": { "enabled": true } } }
          },
          "agents": {
            "defaults": { "toolTimeoutSeconds": 42, "memory": { "enabled": true, "indexing": "auto" } },
            "alpha": {
              "provider": "github-copilot",
              "model": "m1",
              "displayName": "Alpha",
              "enabled": true,
              "metadata": { "team": "platform" },
              "extensions": { "widget": { "on": true } }
            }
          }
        }
        """;

    /// <summary>
    /// Seeds a populated store and guarantees no config.json exists - the store-only home shape.
    /// </summary>
    private async Task SeedStoreOnlyAsync(string json = SeedJson)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        await ConfigStoreBootstrap.PopulateAsync(StorePath, document);
        ConfigStoreBootstrap.ReleaseConnections(StorePath);

        if (File.Exists(_configPath))
            File.Delete(_configPath);
    }

    /// <summary>
    /// Binds and post-configures exactly the way the options pipeline does, through the real
    /// provider pipeline for the home at <see cref="_configPath"/>.
    /// </summary>
    private PlatformConfig BindThroughPipeline()
    {
        var configuration = new ConfigurationBuilder()
            .AddPlatformConfiguration(_configPath, fileSystem: _fileSystem)
            .Build();

        var config = new PlatformConfig();
        configuration.Bind(config);

        PlatformConfigPostConfigure
            .ForConfigPath(configuration, _configPath, _fileSystem)
            .PostConfigure(Options.DefaultName, config);

        return config;
    }

    /// <summary>Clause 1: agents.defaults survives and is not removed from config.Agents.</summary>
    [Fact]
    public async Task StoreOnlyHome_ExtractsAgentDefaults_AndDoesNotDiscardThem()
    {
        await SeedStoreOnlyAsync();

        var config = BindThroughPipeline();

        config.AgentDefaults.ShouldNotBeNull();
        config.AgentDefaults!.ToolTimeoutSeconds.ShouldBe(42);
        config.AgentDefaults.Memory.ShouldNotBeNull();
        config.AgentDefaults.Memory!.Enabled.ShouldBeTrue();

        // The reserved key is still removed from the agent dictionary - that part of the contract is
        // unchanged. What must not happen is removal WITHOUT extraction, which is the defect.
        config.Agents.ShouldNotBeNull();
        config.Agents!.ShouldNotContainKey("defaults");
        config.Agents.ShouldContainKey("alpha");
    }

    /// <summary>Clause 2: version is populated from the store, not left at its default.</summary>
    [Fact]
    public async Task StoreOnlyHome_PopulatesVersionFromTheStore()
    {
        await SeedStoreOnlyAsync();

        var config = BindThroughPipeline();

        config.PlatformVersion.ShouldBe(2);
    }

    /// <summary>
    /// Clause 3: MigrateLegacyGatewaySettings and PopulateJsonElementFields both run against the
    /// rehydrated document.
    /// </summary>
    [Fact]
    public async Task StoreOnlyHome_RunsLegacyMigrationAndJsonElementPopulation()
    {
        await SeedStoreOnlyAsync();

        var config = BindThroughPipeline();

        // Legacy root-level gateway field migrated onto gateway.*
        config.Gateway.ShouldNotBeNull();
        config.Gateway!.ListenUrl.ShouldBe("http://legacy-root:5099");

        // gateway.extensions.defaults is Dictionary<string, JsonElement> - IConfiguration cannot bind it.
        config.Gateway.Extensions.ShouldNotBeNull();
        config.Gateway.Extensions!.Defaults.ShouldNotBeNull();
        config.Gateway.Extensions.Defaults!.ShouldContainKey("sample");

        // Per-agent JsonElement fields.
        var alpha = config.Agents!["alpha"];
        alpha.Metadata.ShouldNotBeNull();
        alpha.Metadata!.Value.GetProperty("team").GetString().ShouldBe("platform");
        alpha.Extensions.ShouldNotBeNull();
        alpha.Extensions!.ShouldContainKey("widget");
    }

    /// <summary>
    /// Clause 4: with a config.json present the FILE remains the raw-JSON source. Seeded with a store
    /// carrying a different version so a wrong precedence order is detectable rather than masked by
    /// identical content.
    /// </summary>
    [Fact]
    public async Task ConfigFilePresent_TheFileRemainsTheRawJsonSource()
    {
        await SeedStoreOnlyAsync();

        const string FileJson = """
            {
              "version": 7,
              "agents": {
                "defaults": { "toolTimeoutSeconds": 99 },
                "alpha": { "provider": "github-copilot", "model": "m1", "displayName": "Alpha", "enabled": true }
              }
            }
            """;
        File.WriteAllText(_configPath, FileJson);

        var config = BindThroughPipeline();

        config.PlatformVersion.ShouldBe(7);
        config.AgentDefaults.ShouldNotBeNull();
        config.AgentDefaults!.ToolTimeoutSeconds.ShouldBe(99);
    }

    /// <summary>
    /// Clause 5: with neither file nor store the defaults-only fallback still applies and does not throw.
    /// Pins the fallback so the fix does not overreach.
    /// </summary>
    [Fact]
    public void NoFileAndNoStore_StillFallsBackWithoutThrowing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["agents:defaults:model"] = "m0",
                ["agents:alpha:provider"] = "github-copilot",
            })
            .Build();

        var config = new PlatformConfig();
        configuration.Bind(config);
        config.Agents.ShouldNotBeNull();
        config.Agents!.ShouldContainKey("defaults");

        Should.NotThrow(() => PlatformConfigPostConfigure
            .ForConfigPath(configuration, _configPath, _fileSystem)
            .PostConfigure(Options.DefaultName, config));

        // Unchanged fallback contract: the reserved key is stripped when there is genuinely no document.
        config.Agents.ShouldNotBeNull();
        config.Agents!.ShouldNotContainKey("defaults");
    }

    /// <summary>
    /// Clause 6: a store whose rehydration cannot produce a usable document must not crash IOptions
    /// resolution. Two entries describing incompatible shapes ("a" as a leaf and "a.b" beneath it)
    /// make <see cref="ConfigDocumentRehydrator"/> throw; the guard must contain it.
    /// </summary>
    [Fact]
    public async Task StoreThatCannotRehydrate_DoesNotCrashOptionsResolution()
    {
        var store = new SqliteConfigStore($"Data Source={StorePath}");
        await store.ApplyChangesAsync(new ConfigChangeSet(
            [
                new ConfigEntry("conflict", ConfigValueState.Value, "1"),
                new ConfigEntry("conflict.nested", ConfigValueState.Value, "2"),
            ],
            []));
        ConfigStoreBootstrap.ReleaseConnections(StorePath);

        if (File.Exists(_configPath))
            File.Delete(_configPath);

        var configuration = new ConfigurationBuilder()
            .AddPlatformConfiguration(_configPath, fileSystem: _fileSystem)
            .Build();
        var config = new PlatformConfig();
        configuration.Bind(config);

        Should.NotThrow(() => PlatformConfigPostConfigure
            .ForConfigPath(configuration, _configPath, _fileSystem)
            .PostConfigure(Options.DefaultName, config));
    }

    /// <summary>
    /// Clause 7 (mutation guard): the store fallback must be what supplies the raw JSON. Constructing
    /// the post-configure WITHOUT a store - the pre-fix shape - must reproduce the defect exactly,
    /// so reverting the fix cannot leave these tests green.
    /// </summary>
    [Fact]
    public async Task WithoutTheStoreFallback_TheDefectReproduces()
    {
        await SeedStoreOnlyAsync();

        var configuration = new ConfigurationBuilder()
            .AddPlatformConfiguration(_configPath, fileSystem: _fileSystem)
            .Build();
        var config = new PlatformConfig();
        configuration.Bind(config);

        // No store threaded through: this is precisely the pre-#3842 construction.
        new PlatformConfigPostConfigure(configuration, _configPath)
            .PostConfigure(Options.DefaultName, config);

        config.AgentDefaults.ShouldBeNull();
        config.PlatformVersion.ShouldNotBe(2);
        config.Agents.ShouldNotBeNull();
        config.Agents!.ShouldNotContainKey("defaults");
    }

    public void Dispose()
    {
        ConfigStoreBootstrap.ReleaseConnections(StorePath);
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }
}
