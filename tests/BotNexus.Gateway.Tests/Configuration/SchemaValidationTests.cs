using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests.Configuration;

public sealed class SchemaValidationTests
{
    [Fact]
    public void Validate_WithCompleteValidConfig_ReturnsNoErrors()
    {
        var config = new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ListenUrl = "http://localhost:5005",
                DefaultAgentId = BotNexus.Domain.Primitives.AgentId.From("assistant"),
                AgentsDirectory = "agents",
                SessionsDirectory = "sessions",
                LogLevel = "Information",
                SessionStore = new SessionStoreConfig
                {
                    Type = "File",
                    FilePath = "sessions-store"
                },
                Cors = new CorsConfig
                {
                    AllowedOrigins = ["https://app.example.test"]
                },
                ApiKeys = new Dictionary<string, ApiKeyConfig>
                {
                    ["tenant-a"] = new()
                    {
                        ApiKey = "secret",
                        TenantId = "tenant-a",
                        Permissions = ["chat:send"]
                    }
                }
            },
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new()
                {
                    ApiKey = "provider-key",
                    BaseUrl = "https://api.githubcopilot.com",
                    DefaultModel = "gpt-4.1"
                }
            },
            Channels = new Dictionary<string, ChannelConfig>
            {
                ["web"] = new()
                {
                    Type = "signalr",
                    Enabled = true
                }
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
                {
                    Provider = "copilot",
                    Model = "gpt-4.1",
                    Enabled = true
                }
            }
        };

        var errors = PlatformConfigLoader.Validate(config);

        errors.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WithMissingRequiredFields_ReturnsActionableErrors()
    {
        var config = new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new()
            },
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
            }
        };

        var errors = PlatformConfigLoader.Validate(config);

        errors.ShouldNotContain(error => error.Contains("providers.copilot must define apiKey or baseUrl", StringComparison.Ordinal),
            "apiKey/baseUrl are optional — auth can come from auth.json or environment");
        errors.ShouldContain(error => error.Contains("agents.assistant.provider", StringComparison.Ordinal));
        errors.ShouldContain(error => error.Contains("agents.assistant.model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_WithInvalidTypes_ThrowsValidationException()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-schema-validation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var configPath = Path.Combine(rootPath, "config.json");

        try
        {
            await File.WriteAllTextAsync(configPath, """
                                                 {
                                                   "agents": {
                                                     "assistant": {
                                                       "provider": "copilot",
                                                       "model": "gpt-4.1",
                                                       "enabled": "not-a-bool"
                                                     }
                                                   }
                                                 }
                                                 """);

            Func<Task> act = () => PlatformConfigLoader.LoadAsync(configPath);

            (await act.ShouldThrowAsync<OptionsValidationException>())
                .Message.ShouldContain("Invalid JSON");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WithUnknownFields_DoesNotCrashWhenValidationIsDeferred()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-schema-validation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var configPath = Path.Combine(rootPath, "config.json");

        try
        {
            await File.WriteAllTextAsync(configPath, """
                                                 {
                                                   "gateway": {
                                                     "listenUrl": "http://localhost:5005",
                                                     "unknownGatewayField": "ignored"
                                                   },
                                                   "providers": {
                                                     "copilot": {
                                                       "apiKey": "provider-key",
                                                       "unknownProviderField": "ignored"
                                                     }
                                                   },
                                                   "unknownRootField": "ignored"
                                                 }
                                                 """);

            var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
            var errors = PlatformConfigLoader.Validate(config);

            config.Gateway?.ListenUrl.ShouldBe("http://localhost:5005");
            config.Providers.ShouldContainKey("copilot");
            config.Providers!["copilot"].ApiKey.ShouldBe("provider-key");
            errors.ShouldNotBeNull();
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAsync_WithLegacyRootGatewayFields_MigratesToGatewaySection()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-schema-validation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var configPath = Path.Combine(rootPath, "config.json");

        try
        {
            await File.WriteAllTextAsync(configPath, """{"listenUrl":"http://localhost:5005","defaultAgentId":"assistant"}""");

            var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
            var errors = PlatformConfigLoader.Validate(config);

            errors.ShouldBeEmpty();
            config.Gateway?.ListenUrl.ShouldBe("http://localhost:5005");
            config.Gateway?.DefaultAgentId.ShouldBe("assistant");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_ConfigWithExtensionsDefaultsAndAgentJsonElements_DoesNotCrash()
    {
        // Regression: IConfiguration cannot bind JsonElement. gateway.extensions.defaults
        // and per-agent extensions/metadata/isolationOptions were left undefined after
        // IConfiguration.Bind(), causing serialization crashes in PlatformConfigSchema
        // validation at startup.
        var rootPath = Path.Combine(Path.GetTempPath(), "botnexus-jsonelement-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var configPath = Path.Combine(rootPath, "config.json");

        try
        {
            await File.WriteAllTextAsync(configPath, """
                {
                  "gateway": {
                    "extensions": {
                      "defaults": {
                        "botnexus-skills": { "enabled": true },
                        "botnexus-exec": { "enabled": true }
                      }
                    }
                  },
                  "providers": { "copilot": { "enabled": true, "apiKey": "test" } },
                  "agents": {
                    "agent-b": {
                      "provider": "copilot",
                      "model": "gpt-4.1",
                      "extensions": { "botnexus-mcp": { "enabled": true } },
                      "metadata": { "owner": "test-user" },
                      "isolationOptions": { "timeout": 30 }
                    }
                  }
                }
                """);

            var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
            var errors = PlatformConfigLoader.Validate(config);

            errors.ShouldBeEmpty();
            config.Gateway?.Extensions?.Defaults.ShouldNotBeNull();
            config.Gateway!.Extensions!.Defaults!.ShouldContainKey("botnexus-skills");
            config.Agents!["agent-b"].Extensions.ShouldNotBeNull();
            config.Agents["agent-b"].Extensions!.ShouldContainKey("botnexus-mcp");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }

}


