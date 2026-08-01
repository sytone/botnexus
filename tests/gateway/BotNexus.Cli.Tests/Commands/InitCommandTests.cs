using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for InitCommand default configuration values.
/// </summary>
public sealed class InitCommandTests
{
    [Fact]
    public async Task Init_DefaultConfig_IncludesSkillsWorldDefault()
    {
        // Arrange
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-init-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            var cmd = new InitCommand();

            // Act
            await cmd.ExecuteAsync(tempHome, force: false, verbose: false, CancellationToken.None);

            // Assert - skills world default must be present for discoverability
            var configPath = Path.Combine(tempHome, "config.json");
            var json = await File.ReadAllTextAsync(configPath);
            json.ShouldContain("botnexus-skills");
            json.ShouldContain("\"enabled\": true");
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public async Task Init_DefaultConfig_ListenUrl_BindsToAllInterfaces()
    {
        // Arrange
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-init-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            var cmd = new InitCommand();

            // Act
            var result = await cmd.ExecuteAsync(tempHome, force: false, verbose: false, CancellationToken.None);

            // Assert - listenUrl must bind to all interfaces so NetBird/remote access works
            var configPath = Path.Combine(tempHome, "config.json");
            var json = await File.ReadAllTextAsync(configPath);
            json.ShouldContain("0.0.0.0");
            json.ShouldNotContain("localhost:5005");
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    // ---------------------------------------------------------------------
    // Issue #2636: `botnexus init` must emit the bundled Nexus Trailguide agent
    // from the SAME shared defaults the stage-1 reconciler uses.
    // ---------------------------------------------------------------------

    /// <summary>AC1: init against an empty home emits both agents.</summary>
    [Fact]
    public async Task Init_EmptyHome_EmitsAssistantAndTrailguideAgents()
    {
        using var home = new TempHome();

        await new InitCommand().ExecuteAsync(home.Path, force: false, verbose: false, CancellationToken.None);

        var agents = home.ReadConfig()["agents"] as JsonObject;
        agents.ShouldNotBeNull();
        agents.ShouldContainKey(FreshInstallAgentDefaults.DefaultAgentId);
        agents.ShouldContainKey(BundledPlatformAgents.TrailguideAgentId);

        // The emitted entry must be complete - a user opening config.json after init
        // sees a finished, editable definition, not a stub.
        var trailguide = agents[BundledPlatformAgents.TrailguideAgentId] as JsonObject;
        trailguide.ShouldNotBeNull();
        trailguide["provider"]?.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        trailguide["model"]?.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
        trailguide["enabled"]?.GetValue<bool>().ShouldBeTrue();
    }

    /// <summary>
    /// AC2: the Trailguide entry produced by <c>init</c> is field-identical to the entry the
    /// stage-1 reconciler would insert for the same installation. Compared programmatically
    /// against a live reconciler run, never against a hand-copied literal.
    /// </summary>
    [Fact]
    public async Task Init_TrailguideEntry_IsFieldIdenticalToReconcilerInsertion()
    {
        using var initHome = new TempHome();
        await new InitCommand().ExecuteAsync(initHome.Path, force: false, verbose: false, CancellationToken.None);

        var initConfig = initHome.ReadConfig();
        var initTrailguide = (initConfig["agents"] as JsonObject)?[BundledPlatformAgents.TrailguideAgentId];
        initTrailguide.ShouldNotBeNull();

        // Same installation, minus the Trailguide entry: exactly what a stage-1 reconciler
        // pass would have been handed on an install that predates this change.
        using var reconcileHome = new TempHome();
        var withoutTrailguide = initConfig.DeepClone().AsObject();
        (withoutTrailguide["agents"] as JsonObject)!.Remove(BundledPlatformAgents.TrailguideAgentId);
        reconcileHome.WriteConfig(withoutTrailguide);

        var fileSystem = new FileSystem();
        var service = PlatformAgentReconciliationService.Create(
            new BotNexusHome(fileSystem, reconcileHome.Path, dataPath: reconcileHome.Path),
            fileSystem,
            NullLogger.Instance);
        await service.StartAsync(CancellationToken.None);

        var reconciledTrailguide =
            (reconcileHome.ReadConfig()["agents"] as JsonObject)?[BundledPlatformAgents.TrailguideAgentId];
        reconciledTrailguide.ShouldNotBeNull();

        // Field-by-field parity, both directions (catches extra AND missing keys).
        var initFields = ((JsonObject)initTrailguide).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var reconciledFields = ((JsonObject)reconciledTrailguide).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        initFields.ShouldBe(reconciledFields);

        foreach (var field in initFields)
        {
            JsonNode.DeepEquals(((JsonObject)initTrailguide)[field], ((JsonObject)reconciledTrailguide)[field])
                .ShouldBeTrue($"Field '{field}' differs between init and reconciler output.");
        }

        JsonNode.DeepEquals(initTrailguide, reconciledTrailguide).ShouldBeTrue();
    }

    /// <summary>
    /// AC3: provider/model for both agents comes from one shared source - changing that source
    /// moves both entries.
    /// </summary>
    [Fact]
    public void FreshInstallAgentDefaults_ChangingProviderAndModel_MovesBothAgents()
    {
        var baseline = FreshInstallAgentDefaults.CreateAgents();
        baseline[FreshInstallAgentDefaults.DefaultAgentId]?["provider"]?.GetValue<string>()
            .ShouldBe(FreshInstallAgentDefaults.DefaultProvider);
        baseline[BundledPlatformAgents.TrailguideAgentId]?["provider"]?.GetValue<string>()
            .ShouldBe(FreshInstallAgentDefaults.DefaultProvider);

        // One decision moved -> both agents move. If Trailguide had its own independent
        // default block this assertion would fail.
        var moved = FreshInstallAgentDefaults.CreateAgents("mutant-provider", "mutant-model");

        foreach (var agentId in new[] { FreshInstallAgentDefaults.DefaultAgentId, BundledPlatformAgents.TrailguideAgentId })
        {
            var agent = moved[agentId] as JsonObject;
            agent.ShouldNotBeNull($"Agent '{agentId}' missing from shared fresh-install defaults.");
            agent["provider"]?.GetValue<string>().ShouldBe("mutant-provider", $"provider for '{agentId}'");
            agent["model"]?.GetValue<string>().ShouldBe("mutant-model", $"model for '{agentId}'");
        }
    }

    /// <summary>AC4: gateway.defaultAgentId remains <c>assistant</c>.</summary>
    [Fact]
    public async Task Init_DefaultAgentId_RemainsAssistant()
    {
        using var home = new TempHome();

        await new InitCommand().ExecuteAsync(home.Path, force: false, verbose: false, CancellationToken.None);

        home.ReadConfig()["gateway"]?["defaultAgentId"]?.GetValue<string>().ShouldBe("assistant");
        FreshInstallAgentDefaults.DefaultAgentId.ShouldBe("assistant");
    }

    /// <summary>
    /// AC5: starting the gateway immediately after init performs no Trailguide config write -
    /// the entry is already complete, so reconciliation leaves the file byte-for-byte untouched.
    /// </summary>
    [Fact]
    public async Task ReconcilerAfterInit_PerformsNoTrailguideWrite()
    {
        using var home = new TempHome();
        await new InitCommand().ExecuteAsync(home.Path, force: false, verbose: false, CancellationToken.None);

        var before = await File.ReadAllTextAsync(home.ConfigPath);
        var beforeWrite = File.GetLastWriteTimeUtc(home.ConfigPath);

        var fileSystem = new FileSystem();
        var service = PlatformAgentReconciliationService.Create(
            new BotNexusHome(fileSystem, home.Path, dataPath: home.Path),
            fileSystem,
            NullLogger.Instance);
        await service.StartAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(home.ConfigPath)).ShouldBe(before);
        File.GetLastWriteTimeUtc(home.ConfigPath).ShouldBe(beforeWrite);

        // A write would also have produced a backup.
        var backups = Path.Combine(home.Path, "backups");
        if (Directory.Exists(backups))
            Directory.GetFiles(backups, "*bundled-agent-reconciliation*").ShouldBeEmpty();
    }

    /// <summary>AC6: init honours BOTNEXUS_DATA_DIR for the writable data it produces.</summary>
    [Fact]
    public async Task Init_HonoursDataDirOverride_ForConfigBackups()
    {
        using var home = new TempHome();
        using var dataDir = new TempHome();
        var previous = Environment.GetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar);
        Environment.SetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar, dataDir.Path);
        try
        {
            var cmd = new InitCommand();
            await cmd.ExecuteAsync(home.Path, force: false, verbose: false, CancellationToken.None);

            // #2114 no-op detection means an identical rewrite is skipped entirely (no backup,
            // no file touch). init is deterministic, so a second run alone would produce
            // byte-identical JSON and prove nothing. Perturb the config first so the second run
            // is a REAL write and the backup path is genuinely exercised.
            await File.WriteAllTextAsync(
                home.ConfigPath,
                "{\"gateway\":{\"listenUrl\":\"http://0.0.0.0:9999\"}}");

            await cmd.ExecuteAsync(home.Path, force: true, verbose: false, CancellationToken.None);

            var dataBackups = Path.Combine(dataDir.Path, "backups");
            Directory.Exists(dataBackups).ShouldBeTrue("init must place backups under BOTNEXUS_DATA_DIR.");
            Directory.GetFiles(dataBackups, "config-*.json").ShouldNotBeEmpty();
            Directory.Exists(Path.Combine(home.Path, "backups")).ShouldBeFalse(
                "init must not write backups into the config root when BOTNEXUS_DATA_DIR is set.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BotNexusHome.DataDirOverrideEnvVar, previous);
        }
    }

    private sealed class TempHome : IDisposable
    {
        public TempHome()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"botnexus-init-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string ConfigPath => System.IO.Path.Combine(Path, "config.json");

        public JsonObject ReadConfig() => JsonNode.Parse(File.ReadAllText(ConfigPath))!.AsObject();

        public void WriteConfig(JsonObject config) => File.WriteAllText(ConfigPath, config.ToJsonString());

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Best effort temp cleanup.
            }
        }
    }
}
