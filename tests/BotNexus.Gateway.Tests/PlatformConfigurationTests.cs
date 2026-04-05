using System.Text.Json;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Sessions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests;

public sealed class PlatformConfigurationTests
{
    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WhenFileMissing_ReturnsDefaultConfig()
    {
        var config = await PlatformConfigLoader.LoadAsync(Path.Combine(Guid.NewGuid().ToString("N"), "missing.json"));

        config.Should().NotBeNull();
        config.DefaultAgentId.Should().BeNull();
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithValidFile_DeserializesConfig()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "valid-config.json");
        var json = """
                   {
                     "listenUrl": "http://localhost:18790",
                     "defaultAgentId": "agent-a",
                     "logLevel": "Debug",
                     "providers": {
                       "copilot": {
                         "apiKey": "test-key",
                         "baseUrl": "https://api.githubcopilot.com",
                         "defaultModel": "gpt-4.1"
                       }
                     }
                   }
                   """;
        await File.WriteAllTextAsync(configPath, json);

        var config = await PlatformConfigLoader.LoadAsync(configPath);

        config.GetListenUrl().Should().Be("http://localhost:18790");
        config.GetDefaultAgentId().Should().Be("agent-a");
        config.GetLogLevel().Should().Be("Debug");
        config.Providers.Should().ContainKey("copilot");
        config.Providers!["copilot"].ApiKey.Should().Be("test-key");
        config.Providers["copilot"].BaseUrl.Should().Be("https://api.githubcopilot.com");
        config.Providers["copilot"].DefaultModel.Should().Be("gpt-4.1");
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithExplicitConfigPath_LoadsSpecifiedFile()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "custom-path.json");
        await File.WriteAllTextAsync(configPath, """
                                                {
                                                  "defaultAgentId": "custom-agent"
                                                }
                                                """);

        var config = await PlatformConfigLoader.LoadAsync(configPath);

        config.DefaultAgentId.Should().Be("custom-agent");
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidValues_ReturnsErrors()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            ListenUrl = "not-a-url",
            LogLevel = "verbose",
            AgentsDirectory = "bad\0path"
        });

        errors.Should().Contain(e => e.Contains("listenUrl", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("logLevel", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("agentsDirectory", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidListenUrl_ReturnsListenUrlError()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig { ListenUrl = "ws://localhost:8080" });

        errors.Should().ContainSingle(e => e.Contains("listenUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidLogLevel_ReturnsLogLevelError()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig { LogLevel = "chatty" });

        errors.Should().ContainSingle(e => e.Contains("logLevel", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithMissingProviderFields_ReturnsProviderErrors()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new()
            }
        });

        errors.Should().ContainSingle(e => e.Contains("providers.copilot must define apiKey or baseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidProviderValues_ReturnsProviderErrors()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new()
                {
                    ApiKey = "test",
                    BaseUrl = "ftp://invalid-endpoint"
                }
            }
        });

        errors.Should().ContainSingle(e => e.Contains("providers.copilot.baseUrl", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_EnsureConfigDirectory_WhenMissing_CreatesDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "botnexus-config-dir-tests", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        PlatformConfigLoader.EnsureConfigDirectory(path);

        Directory.Exists(path).Should().BeTrue();
        Directory.Delete(path, recursive: true);
    }

    [Fact]
    public void PlatformConfig_DefaultCtor_InitializesAllPropertiesToNull()
    {
        var config = new PlatformConfig();

        config.ListenUrl.Should().BeNull();
        config.DefaultAgentId.Should().BeNull();
        config.AgentsDirectory.Should().BeNull();
        config.SessionsDirectory.Should().BeNull();
        config.ApiKey.Should().BeNull();
        config.ApiKeys.Should().BeNull();
        config.Gateway.Should().BeNull();
        config.Agents.Should().BeNull();
        config.Channels.Should().BeNull();
        config.LogLevel.Should().BeNull();
        config.Providers.Should().BeNull();
    }

    [Fact]
    public void ProviderConfig_CanStoreApiKeyBaseUrlAndDefaultModel()
    {
        var provider = new ProviderConfig
        {
            ApiKey = "test-key",
            BaseUrl = "https://example.test",
            DefaultModel = "model-x"
        };

        provider.ApiKey.Should().Be("test-key");
        provider.BaseUrl.Should().Be("https://example.test");
        provider.DefaultModel.Should().Be("model-x");
    }

    [Fact]
    public void AddPlatformConfiguration_AppliesGatewayDefaultsAndStoragePaths()
    {
        using var fixture = new PlatformConfigFixture();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();
        services.AddPlatformConfiguration(fixture.ConfigPath);

        using var provider = services.BuildServiceProvider();
        var gatewayOptions = provider.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var sessionStore = provider.GetRequiredService<ISessionStore>();
        var agentSources = provider.GetServices<IAgentConfigurationSource>();

        gatewayOptions.DefaultAgentId.Should().Be("config-agent");
        sessionStore.Should().BeOfType<FileSessionStore>();
        agentSources.Should().ContainSingle();
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithMissingApiKeyFields_ReturnsActionableErrors()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            ApiKeys = new Dictionary<string, ApiKeyConfig>
            {
                ["tenant-a"] = new()
            }
        });

        errors.Should().Contain(e => e.Contains("gateway.apiKeys.tenant-a.apiKey", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("gateway.apiKeys.tenant-a.tenantId", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("gateway.apiKeys.tenant-a.permissions", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithMissingAgentFields_ReturnsActionableErrors()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new()
            }
        });

        errors.Should().Contain(e => e.Contains("agents.assistant.provider", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("agents.assistant.model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithInvalidConfig_ThrowsValidationException()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "invalid-config.json");
        await File.WriteAllTextAsync(configPath, """{"apiKeys":{"tenant-a":{}}}""");

        Func<Task> act = async () => await PlatformConfigLoader.LoadAsync(configPath);

        await act.Should().ThrowAsync<OptionsValidationException>()
            .WithMessage("*gateway.apiKeys.tenant-a.apiKey*");
    }

    private sealed class PlatformConfigFixture : IDisposable
    {
        public PlatformConfigFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "botnexus-platform-config-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);

            ConfigPath = Path.Combine(RootPath, "config.json");
            var config = new PlatformConfig
            {
                DefaultAgentId = "config-agent",
                AgentsDirectory = "agents",
                SessionsDirectory = "sessions",
                LogLevel = "Information",
                ApiKeys = new Dictionary<string, ApiKeyConfig>
                {
                    ["tenant-a"] = new()
                    {
                        ApiKey = "tenant-a-secret",
                        TenantId = "tenant-a",
                        Permissions = ["chat:send"]
                    }
                }
            };

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config));
        }

        public string RootPath { get; }

        public string ConfigPath { get; }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
    }
}
