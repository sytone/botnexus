using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Cli.Commands;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BotNexus.Cli.Tests.Commands;

/// <summary>
/// Tests for InitCommand default configuration values.
/// <para>
/// Issue #2798 note for future readers: the listenUrl expectation in this class was INVERTED, not
/// added. #96 pinned a 0.0.0.0 default here; #2798 established that a wildcard bind is an operator
/// choice, not an out-of-box state, and moved the wildcard to an explicit opt-in. If you find a
/// listenUrl assertion here failing, the fix is almost certainly in InitCommand, not in the test -
/// see the per-test history comment on Init_DefaultConfig_ListenUrl_BindsToLoopbackOnly before
/// changing anything.
/// </para>
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

    /// <summary>
    /// Issue #2798 AC1: `botnexus init` on a clean home writes a LOOPBACK listenUrl.
    ///
    /// HISTORY - DO NOT "RESTORE" THE OLD ASSERTION.
    /// This test previously asserted the opposite: `json.ShouldContain("0.0.0.0")`, under the name
    /// Init_DefaultConfig_ListenUrl_BindsToAllInterfaces, with the comment "listenUrl must bind to
    /// all interfaces so NetBird/remote access works". That expectation was added by #96, which
    /// widened the generated default to 0.0.0.0 so remote/mesh access worked with no extra
    /// configuration. #2798 established that this optimised one deployment shape at the cost of
    /// every local-only install: a fresh install silently published the portal, the SignalR hub, the
    /// agent REST API and the gateway admin endpoints on every interface, and #506 records that the
    /// admin endpoints lack an authorization scope check. The operator was neither asked nor told.
    ///
    /// The test was therefore INVERTED rather than deleted - its inputs (a clean temp home, a single
    /// init run, an assertion over the generated config.json text) are the regression corpus and are
    /// preserved verbatim. Only the expectation moved. The remote case did not disappear; it became
    /// an explicit opt-in, covered by Init_ListenAllInterfaces_WritesWildcardListenUrl below.
    ///
    /// #2798 AC6 non-vacuity: reverting InitCommand's default to "http://0.0.0.0:5005" must redden
    /// THIS test by name.
    /// </summary>
    [Fact]
    public async Task Init_DefaultConfig_ListenUrl_BindsToLoopbackOnly()
    {
        // Arrange
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-init-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            var cmd = new InitCommand();

            // Act
            var result = await cmd.ExecuteAsync(tempHome, force: false, verbose: false, CancellationToken.None);
            result.ShouldBe(0);

            // Assert - a fresh install must not publish the gateway on every interface (#2798).
            var configPath = Path.Combine(tempHome, "config.json");
            var json = await File.ReadAllTextAsync(configPath);
            json.ShouldNotContain("0.0.0.0");

            var listenUrl = JsonNode.Parse(json)!["gateway"]?["listenUrl"]?.GetValue<string>();
            listenUrl.ShouldBe(GatewayBindAddress.LoopbackListenUrl);
            GatewayBindAddress.IsWildcard(listenUrl).ShouldBeFalse();
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    /// <summary>
    /// #2798 AC2: the explicit opt-in produces the wildcard listenUrl, byte-identical in that field
    /// to what init generated before #2798. An operator who wants NetBird/mesh access gets exactly
    /// the old value - the capability moved from silent default to stated choice, it was not removed.
    /// </summary>
    [Fact]
    public async Task Init_ListenAllInterfaces_WritesWildcardListenUrl()
    {
        var tempHome = Path.Combine(Path.GetTempPath(), $"botnexus-init-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);

        try
        {
            var result = await new InitCommand().ExecuteAsync(
                tempHome,
                force: false,
                listenAllInterfaces: true,
                verbose: false,
                cancellationToken: CancellationToken.None);
            result.ShouldBe(0);

            var json = await File.ReadAllTextAsync(Path.Combine(tempHome, "config.json"));
            var listenUrl = JsonNode.Parse(json)!["gateway"]?["listenUrl"]?.GetValue<string>();

            // Byte-identical to the pre-#2798 generated default.
            listenUrl.ShouldBe("http://0.0.0.0:5005");
            GatewayBindAddress.IsWildcard(listenUrl).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(tempHome))
                Directory.Delete(tempHome, recursive: true);
        }
    }

    /// <summary>
    /// #2798 AC3: an EXISTING config whose listenUrl is a wildcard must be left alone. This is the
    /// half of the issue that reads as a clean pass if it is merely assumed - the change is to the
    /// GENERATED DEFAULT for new installs, and an operator who deliberately bound all interfaces
    /// must not have that silently reverted by an unrelated command.
    ///
    /// Two commands that touch config.json without being asked to set listenUrl are exercised:
    /// `doctor config --yes` (which rewrites the file to apply its migrations) and `init` without
    /// --force (which must refuse to overwrite at all).
    /// </summary>
    [Fact]
    public async Task ExistingWildcardListenUrl_IsPreserved_ByCommandsNotSettingIt()
    {
        using var home = new TempHome();

        // A pre-#2798 install: wildcard bind, plus gaps doctor config genuinely wants to fill so the
        // command really does write the file rather than short-circuiting on "nothing to do".
        await File.WriteAllTextAsync(
            home.ConfigPath,
            "{\"gateway\":{\"listenUrl\":\"http://0.0.0.0:5005\"},\"agents\":{\"defaults\":{}}}");

        // init without --force must not touch an existing config at all.
        var initResult = await new InitCommand().ExecuteAsync(
            home.Path, force: false, verbose: false, CancellationToken.None);
        initResult.ShouldBe(0);
        home.ReadConfig()["gateway"]?["listenUrl"]?.GetValue<string>().ShouldBe("http://0.0.0.0:5005");

        // doctor config --yes applies its migrations and rewrites the file; listenUrl is not its
        // business and must survive untouched.
        var doctorResult = await new DoctorConfigCommand().ExecuteAsync(
            home.ConfigPath, autoApply: true, dryRun: false, verbose: false, CancellationToken.None);
        doctorResult.ShouldBe(0);

        var after = home.ReadConfig();
        after["gateway"]?["listenUrl"]?.GetValue<string>().ShouldBe(
            "http://0.0.0.0:5005",
            "#2798 changes the generated default for NEW installs only - an existing wildcard bind is an operator decision.");

        // Sanity: doctor config really did write, so the preservation above is not vacuous.
        after["cron"]?["enabled"]?.GetValue<bool>().ShouldBe(true);
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
