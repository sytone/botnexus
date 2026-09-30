using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Gateway.Configuration.Store;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Configuration.Tests;

public sealed class LegacyGatewayExtensionsMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"botnexus-legacy-extensions-{Guid.NewGuid():N}");

    [Fact]
    public void Loader_MigratesLegacyDefaultsAndLoaderSettings_WithCanonicalValuesWinning()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, LegacyJson);

        var config = PlatformConfigLoader.Load(path, validateOnLoad: false);

        config.Gateway!.ExtensionLoader!.Path.ShouldBe("legacy/extensions");
        config.Gateway.ExtensionLoader.Enabled.ShouldBeFalse();
        config.AgentDefaults!.Extensions!["canonical"].GetProperty("enabled").GetBoolean().ShouldBeTrue();
        config.AgentDefaults.Extensions["legacy-only"].GetProperty("token").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        config.AgentDefaults.Extensions["unknown-shape"].GetProperty("future").GetInt32().ShouldBe(7);
    }

    [Fact]
    public async Task StoreOnlyPostConfigure_MigratesLegacyShapeWithoutTerminatingStartup()
    {
        Directory.CreateDirectory(_directory);
        var configPath = Path.Combine(_directory, "config.json");
        var storePath = ConfigStoreBootstrap.ResolveStorePath(configPath, new FileSystem());
        await ConfigStoreBootstrap.PopulateAsync(storePath, JsonNode.Parse(LegacyJson)!.AsObject());
        ConfigStoreBootstrap.ReleaseConnections(storePath);

        var store = new SqliteConfigStore($"Data Source={storePath}");
        var configuration = new ConfigurationBuilder()
            .Add(new SqliteConfigurationSource { Store = store, StartChangeDetection = false })
            .Build();
        var config = new PlatformConfig();
        configuration.Bind(config);

        Should.NotThrow(() => new PlatformConfigPostConfigure(configuration, configPath)
            .PostConfigure(Options.DefaultName, config));
        var extensions = config.AgentDefaults!.Extensions;
        extensions.ShouldNotBeNull();
        extensions.ShouldContainKey("legacy-only");
        extensions["canonical"].GetProperty("enabled").GetBoolean().ShouldBeTrue();
        config.Gateway!.ExtensionLoader!.Path.ShouldBe("legacy/extensions");
    }

    [Fact]
    public void PostConfigure_LegacyMigrationPreservesEffectiveProviderOverlays()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "config.json");
        File.WriteAllText(path, LegacyJson);

        var configuration = new ConfigurationBuilder()
            .AddResilientJsonFile(path, optional: false, reloadOnChange: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["gateway:extensionLoader:path"] = "overlay/extensions",
                ["gateway:cors:allowedOrigins:0"] = "https://overlay.example"
            })
            .Build();
        var config = new PlatformConfig();
        configuration.Bind(config);

        new PlatformConfigPostConfigure(configuration, path)
            .PostConfigure(Options.DefaultName, config);

        config.Gateway!.ExtensionLoader!.Path.ShouldBe("overlay/extensions");
        config.Gateway.ExtensionLoader.Enabled.ShouldBeFalse();
        config.Gateway.Cors!.AllowedOrigins!.ShouldBe(["https://overlay.example"]);
        config.AgentDefaults!.Extensions!.ShouldContainKey("legacy-only");
    }

    [Fact]
    public void StartupMaterialization_AppliesLegacyLoaderSettings()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "startup-config.json");
        File.WriteAllText(path, LegacyJson);
        var configuration = new ConfigurationBuilder()
            .AddResilientJsonFile(path, optional: false, reloadOnChange: false)
            .Build();
        var config = new PlatformConfig();
        configuration.Bind(config);

        PlatformConfigPostConfigure.ApplyAuthoritativeRawShape(configuration, config);

        config.Gateway!.ExtensionLoader!.Path.ShouldBe("legacy/extensions");
        config.Gateway.ExtensionLoader.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void RawMigration_PreservesUnknownFieldsAndExplicitNulls()
    {
        var document = ConfigDocument.Parse(LegacyJson);

        LegacyGatewayExtensionsMigration.Apply(document).Applied.ShouldBeTrue();

        var migrated = document.ToJsonString();
        migrated.ShouldContain("legacy-only");
        migrated.ShouldContain("unknown-shape");
        migrated.ShouldContain("\"token\": null");
        migrated.ShouldContain("gatewayUnknown");
        LegacyGatewayExtensionsMigration.IsApplicable(document).ShouldBeFalse();
    }

    [Theory]
    [InlineData("{ \"agents\": { \"defaults\": { \"extensions\": null } }, \"gateway\": { \"extensions\": { \"defaults\": { \"legacy\": {} } } } }", "agents.defaults.extensions")]
    [InlineData("{ \"gateway\": { \"extensionLoader\": null, \"extensions\": { \"path\": \"legacy/extensions\" } } }", "gateway.extensionLoader")]
    public void RawMigration_ExplicitCanonicalNullRejectsWithoutChangingDocument(string json, string path)
    {
        var document = ConfigDocument.Parse(json);
        var before = document.ToJsonString();

        var result = LegacyGatewayExtensionsMigration.Apply(document);

        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.Contains(path, StringComparison.Ordinal));
        document.ToJsonString().ShouldBe(before);
    }

    [Theory]
    [InlineData("{ \"gateway\": { \"extensions\": { \"defaults\": 7 } } }", "gateway.extensions.defaults", "object")]
    [InlineData("{ \"gateway\": { \"extensions\": { \"path\": 7 } } }", "gateway.extensions.path", "string or null")]
    [InlineData("{ \"gateway\": { \"extensions\": { \"enabled\": \"yes\" } } }", "gateway.extensions.enabled", "boolean or null")]
    public void ValidateRawJson_MalformedLegacyValueReportsActionableError(
        string json,
        string path,
        string expectedShape)
    {
        var errors = PlatformConfigLoader.ValidateRawJson(json);

        errors.ShouldContain(error => error.Contains(path, StringComparison.Ordinal)
            && error.Contains(expectedShape, StringComparison.Ordinal));
    }

    private const string LegacyJson = """
        {
          "gateway": {
            "gatewayUnknown": { "keep": true },
            "extensions": {
              "path": "legacy/extensions",
              "enabled": false,
              "defaults": {
                "canonical": { "enabled": false, "legacy": "discarded-on-conflict" },
                "legacy-only": { "token": null },
                "unknown-shape": { "future": 7 }
              }
            }
          },
          "agents": {
            "defaults": {
              "extensions": {
                "canonical": { "enabled": true, "canonicalUnknown": 9 }
              }
            }
          }
        }
        """;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException) { }
    }
}
