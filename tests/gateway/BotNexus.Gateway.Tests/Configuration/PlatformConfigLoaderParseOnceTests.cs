using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using BotNexus.Gateway.Configuration;

namespace BotNexus.Gateway.Tests.Configuration;

/// <summary>
/// Pins the parse-once + shared-pipeline contract introduced for #1632. The config load pipeline must
/// (a) deserialize/migrate/extract through a <em>single</em> parsed <see cref="JsonDocument"/> rather than
/// re-parsing the same raw JSON in <c>MigrateLegacyGatewaySettings</c> and <c>ExtractAgentDefaults</c>, and
/// (b) share one materialisation core between <see cref="PlatformConfigLoader.Load"/> /
/// <see cref="PlatformConfigLoader.LoadAsync"/> (via <c>FinishLoad</c>) and
/// <see cref="PlatformConfigLoader.TryRecoverFromBackup"/>, so a new migration/extraction step applies to
/// both the primary-load and backup-recovery paths automatically.
/// </summary>
/// <remarks>
/// The <see cref="JsonElement"/>-accepting overloads of <c>MigrateLegacyGatewaySettings</c> and
/// <c>ExtractAgentDefaults</c> are the parse-once seam: the loader parses once and threads the
/// root element into both, while the legacy <c>(config, rawJson)</c> string overloads are retained
/// (external callers and existing tests use them) by parsing once and delegating.
/// </remarks>
public sealed class PlatformConfigLoaderParseOnceTests
{
    private const string LegacyConfigJson = """
        {
          "version": 1,
          "listenUrl": "http://localhost:5005",
          "logLevel": "Debug",
          "compaction": { "preservedTurns": 7 },
          "providers": { "copilot": { "apiKey": "test-key" } },
          "agents": {
            "defaults": { "toolTimeoutSeconds": 42 },
            "assistant": { "provider": "copilot", "model": "gpt-4.1" }
          }
        }
        """;

    // --- parse-once: JsonElement overloads equal the string overloads -------

    [Fact]
    public void MigrateLegacyGatewaySettings_JsonElementOverload_MatchesStringOverload()
    {
        using var document = JsonDocument.Parse(LegacyConfigJson);

        var viaString = PlatformConfigLoader.MigrateLegacyGatewaySettings(new PlatformConfig(), LegacyConfigJson);
        var viaElement = PlatformConfigLoader.MigrateLegacyGatewaySettings(new PlatformConfig(), document.RootElement);

        viaElement.Gateway.ShouldNotBeNull();
        viaElement.Gateway!.ListenUrl.ShouldBe(viaString.Gateway?.ListenUrl);
        viaElement.Gateway!.ListenUrl.ShouldBe("http://localhost:5005");
        viaElement.Gateway!.LogLevel.ShouldBe(viaString.Gateway?.LogLevel);
        viaElement.Gateway!.LogLevel.ShouldBe("Debug");
        // Object migration (compaction) hoisted identically.
        viaElement.Gateway!.Compaction.ShouldNotBeNull();
        int? stringPreserved = viaString.Gateway?.Compaction?.PreservedTurns;
        viaElement.Gateway!.Compaction!.PreservedTurns.ShouldBe(stringPreserved ?? -1);
        viaElement.Gateway!.Compaction!.PreservedTurns.ShouldBe(7);
    }

    [Fact]
    public void ExtractAgentDefaults_JsonElementOverload_MatchesStringOverload()
    {
        using var document = JsonDocument.Parse(LegacyConfigJson);

        var viaString = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["assistant"] = new AgentDefinitionConfig { Provider = "copilot", Model = "gpt-4.1" }
            }
        };
        var viaElement = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["assistant"] = new AgentDefinitionConfig { Provider = "copilot", Model = "gpt-4.1" }
            }
        };

        PlatformConfigLoader.ExtractAgentDefaults(viaString, LegacyConfigJson);
        PlatformConfigLoader.ExtractAgentDefaults(viaElement, document.RootElement);

        // defaults hoisted to AgentDefaults and stripped from Agents in both.
        viaElement.AgentDefaults.ShouldNotBeNull();
        viaElement.AgentDefaults!.ToolTimeoutSeconds.ShouldBe(viaString.AgentDefaults?.ToolTimeoutSeconds);
        viaElement.AgentDefaults!.ToolTimeoutSeconds.ShouldBe(42);
        viaElement.Agents.ShouldNotBeNull();
        viaElement.Agents!.ContainsKey("defaults").ShouldBeFalse();
        viaElement.Agents!.ContainsKey("assistant").ShouldBeTrue();
        // Raw element capture parity.
        viaElement.AgentRawElements.ShouldNotBeNull();
        viaElement.AgentRawElements!.Keys.ShouldBe(viaString.AgentRawElements!.Keys);
    }

    [Fact]
    public void MigrateLegacyGatewaySettings_JsonElementOverload_NonObjectRoot_ReturnsConfigUnchanged()
    {
        using var document = JsonDocument.Parse("[]");
        var config = new PlatformConfig();

        var result = PlatformConfigLoader.MigrateLegacyGatewaySettings(config, document.RootElement);

        result.ShouldBeSameAs(config);
        result.Gateway.ShouldBeNull();
    }

    [Fact]
    public void ExtractAgentDefaults_JsonElementOverload_NonObjectRoot_LeavesConfigUnchanged()
    {
        using var document = JsonDocument.Parse("[]");
        var config = new PlatformConfig();

        PlatformConfigLoader.ExtractAgentDefaults(config, document.RootElement);

        config.AgentDefaults.ShouldBeNull();
    }

    // --- shared pipeline: primary load == backup recovery -------------------

    [Fact]
    public void Load_AndTryRecoverFromBackup_ProduceEquivalentConfigFromIdenticalJson()
    {
        // The same JSON fed through FinishLoad (primary) and TryRecoverFromBackup (recovery) must
        // materialise identically -- both run the one shared deserialize -> migrate -> extract core.
        var fs = new MockFileSystem();
        var configPath = "/home/user/.botnexus/config.json";
        var backupsDir = "/home/user/.botnexus/backups";
        var backupPath = backupsDir + "/config-20260601-120000-pre-write.json";

        fs.AddFile(configPath, new MockFileData(LegacyConfigJson));
        fs.AddDirectory(backupsDir);
        fs.AddFile(backupPath, new MockFileData(LegacyConfigJson));

        var primary = PlatformConfigLoader.Load(configPath, validateOnLoad: false, fileSystem: fs);
        var recovered = PlatformConfigLoader.TryRecoverFromBackup(configPath, out _, fs);

        recovered.ShouldNotBeNull();

        // Legacy gateway migration applied on both paths.
        primary.Gateway?.ListenUrl.ShouldBe("http://localhost:5005");
        recovered!.Gateway?.ListenUrl.ShouldBe(primary.Gateway?.ListenUrl);
        recovered.Gateway?.LogLevel.ShouldBe(primary.Gateway?.LogLevel);
        int? primaryPreserved = primary.Gateway?.Compaction?.PreservedTurns;
        int? recoveredPreserved = recovered.Gateway?.Compaction?.PreservedTurns;
        recoveredPreserved.ShouldBe(primaryPreserved);

        // Agent-defaults extraction applied on both paths.
        primary.AgentDefaults?.ToolTimeoutSeconds.ShouldBe(42);
        recovered.AgentDefaults?.ToolTimeoutSeconds.ShouldBe(primary.AgentDefaults?.ToolTimeoutSeconds);

        // 'defaults' stripped from Agents on both paths; real agent retained.
        primary.Agents.ShouldNotBeNull();
        recovered.Agents.ShouldNotBeNull();
        primary.Agents!.ContainsKey("defaults").ShouldBeFalse();
        recovered.Agents!.ContainsKey("defaults").ShouldBeFalse();
        recovered.Agents!.Keys.ShouldBe(primary.Agents!.Keys);
    }

    // --- regression: a single primary load still migrates + extracts correctly

    [Fact]
    public void Load_LegacyConfig_MigratesAndExtractsThroughSingleParse()
    {
        var fs = new MockFileSystem();
        var configPath = "/home/user/.botnexus/config.json";
        fs.AddFile(configPath, new MockFileData(LegacyConfigJson));

        var config = PlatformConfigLoader.Load(configPath, validateOnLoad: false, fileSystem: fs);

        config.Gateway?.ListenUrl.ShouldBe("http://localhost:5005");
        config.Gateway?.LogLevel.ShouldBe("Debug");
        config.Gateway?.Compaction?.PreservedTurns.ShouldBe(7);
        config.AgentDefaults?.ToolTimeoutSeconds.ShouldBe(42);
        config.Agents.ShouldNotBeNull();
        config.Agents!.ContainsKey("defaults").ShouldBeFalse();
        config.Agents!.ContainsKey("assistant").ShouldBeTrue();
        config.AgentRawElements.ShouldNotBeNull();
        config.AgentRawElements!.ContainsKey("assistant").ShouldBeTrue();
    }
}
