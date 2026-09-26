using System.Text.Json;
using BotNexus.Gateway.Abstractions.Extensions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json.Nodes;
using Shouldly;

namespace BotNexus.Gateway.Tests.Extensions;

public sealed class ExtensionConfigSchemaTests : IDisposable
{
    private readonly string _rootPath;

    public ExtensionConfigSchemaTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-config-schema-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_rootPath);
    }

    public void Dispose() => Directory.Delete(_rootPath, recursive: true);

    // ExtensionManifest.Enabled

    [Fact]
    public async Task LoadConfiguredExtensionsAsync_SkipsExtension_WhenManifestEnabledIsFalse()
    {
        var dir = CreateExtensionDir("disabled-ext", enabled: false);
        var platformConfig = BuildPlatformConfig(dir);

        var services = new ServiceCollection().AddLogging();
        var results = await services.LoadConfiguredExtensionsAsync(
            platformConfig, NullLoggerFactory.Instance);

        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadConfiguredExtensionsAsync_LoadsExtension_WhenManifestEnabledIsTrue()
    {
        var dir = CreateExtensionDir("enabled-ext", enabled: true);
        var platformConfig = BuildPlatformConfig(dir);

        var services = new ServiceCollection().AddLogging();
        var results = await services.LoadConfiguredExtensionsAsync(
            platformConfig, NullLoggerFactory.Instance);

        results.Count.ShouldBe(1);
        results[0].ExtensionId.ShouldBe("enabled-ext");
    }

    // ExtensionConfigFieldSchema deserialization

    [Fact]
    public void ExtensionManifest_Deserializes_ConfigSchema()
    {
        var json = """
            {
              "id": "my-ext",
              "name": "My Ext",
              "version": "1.0.0",
              "entryAssembly": "my.dll",
              "extensionTypes": ["tool"],
              "configSchema": [
                { "id": "apiKey", "type": "string", "required": true, "sensitive": true, "description": "API key" },
                { "id": "timeout", "type": "integer", "default": "30", "required": false, "description": "Timeout seconds" }
              ]
            }
            """;

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(json, opts)!;

        manifest.ConfigSchema.Count.ShouldBe(2);
        manifest.ConfigSchema[0].Id.ShouldBe("apiKey");
        manifest.ConfigSchema[0].Type.ShouldBe("string");
        manifest.ConfigSchema[0].Required.ShouldBeTrue();
        manifest.ConfigSchema[0].Sensitive.ShouldBeTrue();
        manifest.ConfigSchema[0].Default.ShouldBeNull();
        manifest.ConfigSchema[1].Id.ShouldBe("timeout");
        manifest.ConfigSchema[1].Default.ShouldBe("30");
        manifest.ConfigSchema[1].Required.ShouldBeFalse();
    }

    [Fact]
    public void ExtensionManifest_ConfigSchema_DefaultsToEmpty_WhenNotDeclared()
    {
        var json = """
            {
              "id": "minimal",
              "name": "Minimal",
              "version": "1.0.0",
              "entryAssembly": "m.dll",
              "extensionTypes": ["tool"]
            }
            """;

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(json, opts)!;

        manifest.ConfigSchema.ShouldBeEmpty();
    }

    // ExtensionConfigValidator

    [Fact]
    public void ExtensionConfigValidator_ReturnsValid_WhenAllRequiredFieldsPresent()
    {
        var schema = new[]
        {
            new ExtensionConfigFieldSchema { Id = "apiKey", Type = "string", Required = true }
        };
        var config = JsonDocument.Parse("""{"apiKey":"abc123"}""").RootElement;

        var validator = new ExtensionConfigValidator();
        var result = validator.Validate("my-ext", schema, config);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void ExtensionConfigValidator_ReturnsInvalid_WhenRequiredFieldMissing()
    {
        var schema = new[]
        {
            new ExtensionConfigFieldSchema { Id = "apiKey", Type = "string", Required = true }
        };
        var config = JsonDocument.Parse("{}").RootElement;

        var validator = new ExtensionConfigValidator();
        var result = validator.Validate("my-ext", schema, config);

        result.IsValid.ShouldBeFalse();
        result.Warnings.ShouldHaveSingleItem();
        result.Warnings[0].ShouldContain("apiKey");
    }

    [Fact]
    public void ExtensionConfigValidator_AppliesDefault_WhenOptionalFieldMissingAndDefaultDeclared()
    {
        var schema = new[]
        {
            new ExtensionConfigFieldSchema { Id = "timeout", Type = "integer", Required = false, Default = "30" }
        };
        var config = JsonDocument.Parse("{}").RootElement;

        var validator = new ExtensionConfigValidator();
        var result = validator.Validate("my-ext", schema, config);

        result.IsValid.ShouldBeTrue();
        result.AppliedDefaults.ShouldContainKey("timeout");
        result.AppliedDefaults["timeout"].ShouldBe("30");
    }

    [Fact]
    public void ExtensionConfigValidator_NoWarning_WhenOptionalFieldMissingAndNoDefault()
    {
        var schema = new[]
        {
            new ExtensionConfigFieldSchema { Id = "optionalFlag", Type = "bool", Required = false }
        };
        var config = JsonDocument.Parse("{}").RootElement;

        var validator = new ExtensionConfigValidator();
        var result = validator.Validate("my-ext", schema, config);

        result.IsValid.ShouldBeTrue();
        result.Warnings.ShouldBeEmpty();
        result.AppliedDefaults.ShouldBeEmpty();
    }


    [Fact]
    public void StartupDiagnostics_LogExactUnsupportedAndUnknownPaths_WithoutMutation()
    {
        var platformConfig = new PlatformConfig
        {
            World = new WorldSettingsConfig
            {
                Extensions = new Dictionary<string, JsonElement>
                {
                    ["agent-only"] = JsonDocument.Parse("""{ "enabled": true }""").RootElement.Clone()
                }
            },
            Gateway = new GatewaySettingsConfig
            {
                Extensions = new Dictionary<string, JsonElement>
                {
                    ["unknown"] = JsonDocument.Parse("{}").RootElement.Clone()
                }
            }
        };
        var logger = new RecordingLogger();
        var before = JsonSerializer.Serialize(platformConfig);

        ServiceCollectionExtensions.LogConfigurationScopeDiagnostics(
            platformConfig,
            [Loaded("agent-only", ExtensionConfigurationScope.Agent)],
            logger);

        JsonSerializer.Serialize(platformConfig).ShouldBe(before);
        logger.Messages.ShouldContain(message => message.Contains("world.extensions.agent-only", StringComparison.Ordinal));
        logger.Messages.ShouldContain(message => message.Contains("gateway.extensions.unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void StartupDiagnostics_IncludeAgentDefaultExtensions()
    {
        var platformConfig = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                Extensions = new Dictionary<string, JsonElement>
                {
                    ["gateway-only"] = JsonSerializer.SerializeToElement(new { enabled = true })
                }
            }
        };
        var logger = new RecordingLogger();

        ServiceCollectionExtensions.LogConfigurationScopeDiagnostics(
            platformConfig,
            [Loaded("gateway-only", ExtensionConfigurationScope.Gateway)],
            logger);

        logger.Messages.ShouldContain(message =>
            message.Contains("agents.defaults.extensions.gateway-only", StringComparison.Ordinal));
    }

    private static LoadedExtension Loaded(string id, params ExtensionConfigurationScope[] scopes) => new()
    {
        ExtensionId = id,
        Name = id,
        Version = "1.0.0",
        DirectoryPath = id,
        EntryAssemblyPath = id,
        LoadedAtUtc = DateTimeOffset.UnixEpoch,
        ConfigurationScopes = scopes
    };

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    // Helpers

    private string CreateExtensionDir(string id, bool enabled)
    {
        var dir = Path.Combine(_rootPath, id);
        Directory.CreateDirectory(dir);
        var manifest = new ExtensionManifest
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            EntryAssembly = "entry.dll",
            ExtensionTypes = ["tool"],
            Enabled = enabled
        };
        File.WriteAllText(
            Path.Combine(dir, "botnexus-extension.json"),
            JsonSerializer.Serialize(manifest));
        File.WriteAllText(Path.Combine(dir, "entry.dll"), "placeholder");
        return dir;
    }

    private PlatformConfig BuildPlatformConfig(string extensionsDir) => new()
    {
        Gateway = new()
        {
            ExtensionLoader = new ExtensionLoaderConfig
            {
                Enabled = true,
                Path = Path.GetDirectoryName(extensionsDir)
            }
        }
    };
}
