using BotNexus.Gateway.Configuration;
using FluentAssertions;
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
                DefaultAgentId = "assistant",
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

        errors.Should().BeEmpty();
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

        errors.Should().NotContain(error => error.Contains("providers.copilot must define apiKey or baseUrl", StringComparison.Ordinal),
            "apiKey/baseUrl are optional — auth can come from auth.json or environment");
        errors.Should().Contain(error => error.Contains("agents.assistant.provider", StringComparison.Ordinal));
        errors.Should().Contain(error => error.Contains("agents.assistant.model", StringComparison.Ordinal));
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

            await act.Should().ThrowAsync<OptionsValidationException>()
                .WithMessage("*Invalid JSON*");
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

            config.Gateway?.ListenUrl.Should().Be("http://localhost:5005");
            config.Providers.Should().ContainKey("copilot");
            config.Providers!["copilot"].ApiKey.Should().Be("provider-key");
            errors.Should().NotBeNull();
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

            errors.Should().BeEmpty();
            config.Gateway?.ListenUrl.Should().Be("http://localhost:5005");
            config.Gateway?.DefaultAgentId.Should().Be("assistant");
        }
        finally
        {
            if (Directory.Exists(rootPath))
                Directory.Delete(rootPath, recursive: true);
        }
    }
}

