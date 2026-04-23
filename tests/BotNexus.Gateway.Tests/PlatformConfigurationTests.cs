using System.Diagnostics;
using System.Text.Json;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Configuration;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Extensions;
using BotNexus.Gateway.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BotNexus.Gateway.Tests;

public sealed class PlatformConfigurationTests
{
    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WhenFileMissing_ReturnsDefaultConfig()
    {
        var config = await PlatformConfigLoader.LoadAsync(Path.Combine(Guid.NewGuid().ToString("N"), "missing.json"));

        config.ShouldNotBeNull();
        config.Version.ShouldBe(1);
        config.Gateway.ShouldBeNull();
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

        config.Gateway?.ListenUrl.ShouldBe("http://localhost:18790");
        config.Gateway?.DefaultAgentId.ShouldBe("agent-a");
        config.Gateway?.LogLevel.ShouldBe("Debug");
        config.Providers.ShouldContainKey("copilot");
        config.Providers!["copilot"].ApiKey.ShouldBe("test-key");
        config.Providers["copilot"].BaseUrl.ShouldBe("https://api.githubcopilot.com");
        config.Providers["copilot"].DefaultModel.ShouldBe("gpt-4.1");
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithAgentFileAccess_DeserializesPolicy()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "agent-file-access.json");
        var json = """
                   {
                     "agents": {
                       "agent-a": {
                         "provider": "copilot",
                         "model": "gpt-4.1",
                         "fileAccess": {
                           "allowedReadPaths": ["Q:\\repos\\botnexus\\docs"],
                           "allowedWritePaths": ["Q:\\repos\\botnexus\\artifacts"],
                           "deniedPaths": ["Q:\\repos\\botnexus\\docs\\secrets"]
                         }
                       }
                     }
                   }
                   """;
        await File.WriteAllTextAsync(configPath, json);

        var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        var fileAccess = config.Agents!["agent-a"].FileAccess;

        fileAccess.ShouldNotBeNull();
        fileAccess!.AllowedReadPaths.ShouldHaveSingleItem().ShouldBe(@"Q:\repos\botnexus\docs");
        fileAccess.AllowedWritePaths.ShouldHaveSingleItem().ShouldBe(@"Q:\repos\botnexus\artifacts");
        fileAccess.DeniedPaths.ShouldHaveSingleItem().ShouldBe(@"Q:\repos\botnexus\docs\secrets");
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

        config.Gateway?.DefaultAgentId.ShouldBe("custom-agent");
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidValues_ReturnsErrors()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ListenUrl = "not-a-url",
                LogLevel = "verbose",
                AgentsDirectory = "bad\0path"
            }
        });

        errors.ShouldContain(e => e.Contains("listenUrl", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("logLevel", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("agentsDirectory", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidListenUrl_ReturnsListenUrlError()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ListenUrl = "ws://localhost:8080"
            }
        });

        errors.Where(e => e.Contains("listenUrl", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidLogLevel_ReturnsLogLevelError()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                LogLevel = "chatty"
            }
        });

        errors.Where(e => e.Contains("logLevel", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithMissingProviderFields_AllowsOptionalApiKey()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["copilot"] = new()
            }
        });

        errors.ShouldNotContain(e => e.Contains("providers.copilot must define apiKey or baseUrl", StringComparison.Ordinal),
            "apiKey/baseUrl are optional — auth can come from auth.json or environment");
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

        errors.Where(e => e.Contains("providers.copilot.baseUrl", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidCrossWorldSettings_ReturnsErrors()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                CrossWorld = new CrossWorldFederationConfig
                {
                    Peers = new Dictionary<string, CrossWorldPeerConfig>
                    {
                        ["world-b"] = new()
                        {
                            Enabled = true,
                            Endpoint = "ftp://invalid"
                        }
                    },
                    Inbound = new CrossWorldInboundConfig
                    {
                        Enabled = true,
                        AllowedWorlds = ["world-b"],
                        ApiKeys = new Dictionary<string, string>()
                    }
                }
            }
        });

        errors.ShouldContain(e => e.Contains("gateway.crossWorld.peers.world-b.endpoint", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("gateway.crossWorld.inbound.apiKeys.world-b", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_EnsureConfigDirectory_WhenMissing_CreatesDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "botnexus-config-dir-tests", Guid.NewGuid().ToString("N"));
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        PlatformConfigLoader.EnsureConfigDirectory(path);

        Directory.Exists(path).ShouldBeTrue();
        Directory.Delete(path, recursive: true);
    }

    [Fact]
    public void PlatformConfig_DefaultCtor_InitializesAllPropertiesToNull()
    {
        var config = new PlatformConfig();

        config.Version.ShouldBe(1);
        config.ApiKey.ShouldBeNull();
        config.Gateway.ShouldBeNull();
        config.Agents.ShouldBeNull();
        config.Channels.ShouldBeNull();
        config.Cron.ShouldBeNull();
        config.Providers.ShouldBeNull();
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithMissingVersion_DefaultsToVersionOne()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "missing-version.json");
        await File.WriteAllTextAsync(configPath, """{"defaultAgentId":"assistant"}""");

        var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);

        config.Version.ShouldBe(1);
    }

    [Fact]
    public void PlatformConfigLoader_Load_WithMissingVersion_DefaultsToVersionOne()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "missing-version-sync.json");
        File.WriteAllText(configPath, """{"defaultAgentId":"assistant"}""");

        var config = PlatformConfigLoader.Load(configPath, validateOnLoad: false);

        config.Version.ShouldBe(1);
    }

    [Fact]
    public void PlatformConfigLoader_ValidateWarnings_WithUnknownVersion_ReturnsWarning()
    {
        var warnings = PlatformConfigLoader.ValidateWarnings(new PlatformConfig { Version = 2 });

        warnings.Where(warning => warning.Contains("version '2'", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void PlatformConfigLoader_ValidateWarnings_WithKnownVersion_ReturnsNoWarnings()
    {
        var warnings = PlatformConfigLoader.ValidateWarnings(new PlatformConfig { Version = 1 });

        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void PlatformConfigLoader_Load_WithUnknownVersion_EmitsTraceWarning()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "future-version.json");
        File.WriteAllText(configPath, """{"version":2}""");
        using var listener = new CollectingTraceListener();

        Trace.Listeners.Add(listener);
        try
        {
            _ = PlatformConfigLoader.Load(configPath, validateOnLoad: false);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        listener.Messages.ShouldContain(message => message.Contains("Platform config warning", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Load_WithSupportedVersion_DoesNotEmitTraceWarning()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "supported-version.json");
        File.WriteAllText(configPath, """{"version":1}""");
        using var listener = new CollectingTraceListener();

        Trace.Listeners.Add(listener);
        try
        {
            _ = PlatformConfigLoader.Load(configPath, validateOnLoad: false);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        listener.Messages.ShouldNotContain(message => message.Contains("Platform config warning", StringComparison.Ordinal));
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

        provider.ApiKey.ShouldBe("test-key");
        provider.BaseUrl.ShouldBe("https://example.test");
        provider.DefaultModel.ShouldBe("model-x");
    }

    [Fact]
    public void AddPlatformConfiguration_AppliesGatewayDefaultsAndStoragePaths()
    {
        using var fixture = new PlatformConfigFixture();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();
        services.AddSingleton<ILocationResolver>(new StubLocationResolver());
        services.AddPlatformConfiguration(fixture.ConfigPath);

        using var provider = services.BuildServiceProvider();
        var gatewayOptions = provider.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var sessionStore = provider.GetRequiredService<ISessionStore>();
        var agentSources = provider.GetServices<IAgentConfigurationSource>();
        var configurationWriter = provider.GetRequiredService<IAgentConfigurationWriter>();

        gatewayOptions.DefaultAgentId.ShouldBe("config-agent");
        sessionStore.ShouldBeOfType<FileSessionStore>();
        agentSources.ShouldContain(source => source is FileAgentConfigurationSource);
        agentSources.ShouldContain(source => source is PlatformConfigAgentSource);
        configurationWriter.ShouldBeOfType<PlatformConfigAgentWriter>();
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithMissingApiKeyFields_ReturnsActionableErrors()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                ApiKeys = new Dictionary<string, ApiKeyConfig>
                {
                    ["tenant-a"] = new()
                }
            }
        });

        errors.ShouldContain(e => e.Contains("gateway.apiKeys.tenant-a.apiKey", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("gateway.apiKeys.tenant-a.tenantId", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("gateway.apiKeys.tenant-a.permissions", StringComparison.Ordinal));
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

        errors.ShouldContain(e => e.Contains("agents.assistant.provider", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("agents.assistant.model", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithInvalidConfig_ThrowsValidationException()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "invalid-config.json");
        await File.WriteAllTextAsync(configPath, """{"apiKeys":{"tenant-a":{}}}""");

        Func<Task> act = async () => await PlatformConfigLoader.LoadAsync(configPath);

        (await act.ShouldThrowAsync<OptionsValidationException>())
            .Message.ShouldContain("gateway.apiKeys.tenant-a.apiKey");
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidSessionStoreType_ReturnsActionableError()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                SessionStore = new SessionStoreConfig { Type = "Sql" }
            }
        });

        errors.Where(e => e.Contains("gateway.sessionStore.type", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithFileSessionStoreMissingPath_ReturnsActionableError()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                SessionStore = new SessionStoreConfig { Type = "File" }
            }
        });

        errors.Where(e => e.Contains("gateway.sessionStore.filePath", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithSqliteSessionStoreMissingConnectionString_ReturnsActionableError()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                SessionStore = new SessionStoreConfig { Type = "Sqlite" }
            }
        });

        errors.Where(e => e.Contains("gateway.sessionStore.connectionString", StringComparison.Ordinal)).ShouldHaveSingleItem();
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidCronSettings_ReturnsActionableErrors()
    {
        var errors = PlatformConfigLoader.Validate(new PlatformConfig
        {
            Cron = new CronConfig
            {
                TickIntervalSeconds = 0,
                Jobs = new Dictionary<string, CronJobConfig>
                {
                    ["job-1"] = new()
                }
            }
        });

        errors.ShouldContain(e => e.Contains("cron.tickIntervalSeconds", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("cron.jobs.job-1.schedule", StringComparison.Ordinal));
        errors.ShouldContain(e => e.Contains("cron.jobs.job-1.actionType", StringComparison.Ordinal));
    }

    [Fact]
    public void AddPlatformConfiguration_WithInMemorySessionStore_RegistersInMemorySessionStore()
    {
        using var fixture = new PlatformConfigFixture(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                SessionStore = new SessionStoreConfig
                {
                    Type = "InMemory"
                }
            }
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();
        services.AddPlatformConfiguration(fixture.ConfigPath);

        using var provider = services.BuildServiceProvider();
        var sessionStore = provider.GetRequiredService<ISessionStore>();
        sessionStore.ShouldBeOfType<InMemorySessionStore>();
    }

    [Fact]
    public void AddPlatformConfiguration_WithFileSessionStore_RegistersFileSessionStore()
    {
        using var fixture = new PlatformConfigFixture(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                SessionStore = new SessionStoreConfig
                {
                    Type = "File",
                    FilePath = "session-store"
                }
            }
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();
        services.AddPlatformConfiguration(fixture.ConfigPath);

        using var provider = services.BuildServiceProvider();
        var sessionStore = provider.GetRequiredService<ISessionStore>();
        sessionStore.ShouldBeOfType<FileSessionStore>();
    }

    [Fact]
    public void AddPlatformConfiguration_WithSqliteSessionStore_RegistersSqliteSessionStore()
    {
        using var fixture = new PlatformConfigFixture(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                SessionStore = new SessionStoreConfig
                {
                    Type = "Sqlite",
                    ConnectionString = "Data Source=sessions.db"
                }
            }
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();
        services.AddPlatformConfiguration(fixture.ConfigPath);

        using var provider = services.BuildServiceProvider();
        var sessionStore = provider.GetRequiredService<ISessionStore>();
        sessionStore.ShouldBeOfType<SqliteSessionStore>();
    }

    [Fact]
    public void AddPlatformConfiguration_WithNoSessionStoreConfigured_DefaultsToInMemory()
    {
        var root = Path.Combine(Path.GetTempPath(), "botnexus-platform-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(root, "config.json");

        try
        {
            File.WriteAllText(configPath, "{}");

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddBotNexusGateway();
            services.AddPlatformConfiguration(configPath);

            using var provider = services.BuildServiceProvider();
            var sessionStore = provider.GetRequiredService<ISessionStore>();
            sessionStore.ShouldBeOfType<InMemorySessionStore>();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddPlatformConfiguration_WithCompactionConfigured_BindsCompactionOptions()
    {
        using var fixture = new PlatformConfigFixture(new PlatformConfig
        {
            Gateway = new GatewaySettingsConfig
            {
                Compaction = new CompactionOptions
                {
                    PreservedTurns = 5,
                    MaxSummaryChars = 2000,
                    TokenThresholdRatio = 0.4,
                    ContextWindowTokens = 64000,
                    SummarizationModel = "gpt-4.1-mini"
                }
            }
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBotNexusGateway();
        services.AddPlatformConfiguration(fixture.ConfigPath);

        using var provider = services.BuildServiceProvider();
        var compaction = provider.GetRequiredService<IOptions<CompactionOptions>>().Value;

        compaction.PreservedTurns.ShouldBe(5);
        compaction.MaxSummaryChars.ShouldBe(2000);
        compaction.TokenThresholdRatio.ShouldBe(0.4);
        compaction.ContextWindowTokens.ShouldBe(64000);
        compaction.SummarizationModel.ShouldBe("gpt-4.1-mini");
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithMissingOptionalSections_UsesDefaultsAndValidates()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "minimal-config.json");
        await File.WriteAllTextAsync(configPath, """{"gateway":{"listenUrl":"http://localhost:5005"}}""");

        var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        var errors = PlatformConfigLoader.Validate(config);

        config.Gateway?.ListenUrl.ShouldBe("http://localhost:5005");
        config.Providers.ShouldBeNull();
        config.Channels.ShouldBeNull();
        config.Agents.ShouldBeNull();
        errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithEmptyCollections_LoadsWithoutValidationErrors()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "empty-collections.json");
        await File.WriteAllTextAsync(configPath, """
                                                 {
                                                   "providers": {},
                                                   "channels": {},
                                                   "agents": {}
                                                 }
                                                 """);

        var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        var errors = PlatformConfigLoader.Validate(config);

        config.Providers.ShouldNotBeNull();
        config.Providers!.ShouldBeEmpty();
        config.Channels.ShouldNotBeNull();
        config.Channels!.ShouldBeEmpty();
        config.Agents.ShouldNotBeNull();
        config.Agents!.ShouldBeEmpty();
        errors.ShouldBeEmpty();
    }

    [Fact]
    public void PlatformConfigLoader_Watch_CanBeCreatedAndDisposed()
    {
        using var fixture = new PlatformConfigFixture();
        using var watcher = PlatformConfigLoader.Watch(fixture.ConfigPath);

        watcher.ShouldNotBeNull();
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WhenReadConcurrently_IsThreadSafe()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "concurrent-read.json");
        await File.WriteAllTextAsync(configPath, """
                                                 {
                                                   "gateway": {
                                                     "listenUrl": "http://localhost:5005",
                                                     "defaultAgentId": "assistant"
                                                   }
                                                 }
                                                 """);

        var loads = Enumerable.Range(0, 32)
            .Select(_ => PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false))
            .ToArray();

        var results = await Task.WhenAll(loads);

        results.Select(config => config.Gateway?.ListenUrl).ShouldAllBe(value => value == "http://localhost:5005");
        results.Select(config => config.Gateway?.DefaultAgentId).ShouldAllBe(value => value == "assistant");
    }

    [Fact]
    public async Task PlatformConfigLoader_RoundTripSaveLoad_PersistsChanges()
    {
        using var fixture = new PlatformConfigFixture();
        var original = await PlatformConfigLoader.LoadAsync(fixture.ConfigPath, validateOnLoad: false);
        original.Gateway ??= new GatewaySettingsConfig();
        original.Gateway.LogLevel = "Warning";
        original.Providers = new Dictionary<string, ProviderConfig>
        {
            ["copilot"] = new()
            {
                ApiKey = "updated-key"
            }
        };

        await File.WriteAllTextAsync(fixture.ConfigPath, JsonSerializer.Serialize(original));
        var reloaded = await PlatformConfigLoader.LoadAsync(fixture.ConfigPath, validateOnLoad: false);

        reloaded.Gateway?.LogLevel.ShouldBe("Warning");
        reloaded.Providers.ShouldContainKey("copilot");
        reloaded.Providers!["copilot"].ApiKey.ShouldBe("updated-key");
    }

    private sealed class PlatformConfigFixture : IDisposable
    {
        public PlatformConfigFixture(PlatformConfig? configOverride = null)
        {
            RootPath = Path.Combine(Path.GetTempPath(), "botnexus-platform-config-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);

            ConfigPath = Path.Combine(RootPath, "config.json");
            var config = new PlatformConfig
            {
                Gateway = new GatewaySettingsConfig
                {
                    DefaultAgentId = BotNexus.Domain.Primitives.AgentId.From("config-agent"),
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
                }
            };

            if (configOverride is not null)
            {
                config.Gateway = configOverride.Gateway;
                config.Agents = configOverride.Agents;
                config.Providers = configOverride.Providers;
                config.Channels = configOverride.Channels;
                config.Cron = configOverride.Cron;
                config.ApiKey = configOverride.ApiKey;
                if (configOverride.Gateway is not null)
                {
                    config.Gateway ??= new GatewaySettingsConfig();
                    config.Gateway.ListenUrl = configOverride.Gateway.ListenUrl ?? config.Gateway.ListenUrl;
                    config.Gateway.DefaultAgentId = configOverride.Gateway.DefaultAgentId ?? config.Gateway.DefaultAgentId;
                    config.Gateway.AgentsDirectory = configOverride.Gateway.AgentsDirectory ?? config.Gateway.AgentsDirectory;
                    config.Gateway.SessionsDirectory = configOverride.Gateway.SessionsDirectory ?? config.Gateway.SessionsDirectory;
                    config.Gateway.SessionStore = configOverride.Gateway.SessionStore ?? config.Gateway.SessionStore;
                    config.Gateway.Compaction = configOverride.Gateway.Compaction ?? config.Gateway.Compaction;
                    config.Gateway.Cors = configOverride.Gateway.Cors ?? config.Gateway.Cors;
                    config.Gateway.RateLimit = configOverride.Gateway.RateLimit ?? config.Gateway.RateLimit;
                    config.Gateway.LogLevel = configOverride.Gateway.LogLevel ?? config.Gateway.LogLevel;
                    config.Gateway.ApiKeys = configOverride.Gateway.ApiKeys ?? config.Gateway.ApiKeys;
                    config.Gateway.Extensions = configOverride.Gateway.Extensions ?? config.Gateway.Extensions;
                }
            }

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

    private sealed class CollectingTraceListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message)
        {
            if (!string.IsNullOrEmpty(message))
                Messages.Add(message);
        }

        public override void WriteLine(string? message)
            => Write(message);

        public override void TraceEvent(TraceEventCache? eventCache, string? source, TraceEventType eventType, int id, string? message)
            => Write(message);

        public override void TraceEvent(
            TraceEventCache? eventCache,
            string? source,
            TraceEventType eventType,
            int id,
            string? format,
            params object?[]? args)
            => Write(args is { Length: > 0 } ? string.Format(format ?? string.Empty, args) : format);
    }

    // -------------------------------------------------------------------------
    // Issue #12: agents.defaults validation (scenario 12)
    // -------------------------------------------------------------------------

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidAgentsDefaultsMemoryIndexing_ReportsExactFieldPath()
    {
        // Arrange — agents.defaults.memory.indexing is empty string (invalid)
        var config = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                Memory = new MemoryAgentConfig
                {
                    Enabled = true,
                    Indexing = ""  // invalid: non-empty required
                }
            }
        };

        // Act
        var errors = PlatformConfigLoader.Validate(config);

        // Assert — error references exact field path
        errors.ShouldContain(e => e.Contains("agents.defaults.memory.indexing", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidAgentsDefaultsHeartbeatInterval_ReportsExactFieldPath()
    {
        // Arrange — intervalMinutes <= 0 is invalid
        var config = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                Heartbeat = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 0 }
            }
        };

        // Act
        var errors = PlatformConfigLoader.Validate(config);

        // Assert
        errors.ShouldContain(e => e.Contains("agents.defaults.heartbeat.intervalMinutes", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithInvalidAgentsDefaultsFileAccessBlankPath_ReportsExactFieldPath()
    {
        // Arrange — blank entry in allowedReadPaths is invalid
        var config = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                FileAccess = new FileAccessPolicyConfig
                {
                    AllowedReadPaths = ["/valid/path", ""]
                }
            }
        };

        // Act
        var errors = PlatformConfigLoader.Validate(config);

        // Assert — error must name the exact field path including array index
        errors.ShouldContain(e =>
            e.Contains("agents.defaults.fileAccess.allowedReadPaths", StringComparison.Ordinal) &&
            e.Contains("[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_Validate_WithValidAgentsDefaults_ReturnsNoErrors()
    {
        // Arrange — valid defaults config
        var config = new PlatformConfig
        {
            AgentDefaults = new AgentDefaultsConfig
            {
                ToolIds = ["read", "write"],
                Memory = new MemoryAgentConfig { Enabled = true, Indexing = "auto" },
                Heartbeat = new HeartbeatAgentConfig { Enabled = true, IntervalMinutes = 30 },
                FileAccess = new FileAccessPolicyConfig
                {
                    AllowedReadPaths = ["/home/user/docs"]
                }
            }
        };

        // Act
        var errors = PlatformConfigLoader.Validate(config);

        // Assert
        errors.ShouldBeEmpty();
    }

    // -------------------------------------------------------------------------
    // Issue #12: cron default — enabled = true (scenario 10)
    // -------------------------------------------------------------------------

    [Fact]
    public void CronConfig_WhenCreatedWithDefaults_HasEnabledTrue()
    {
        // Arrange & Act
        var cron = new CronConfig();

        // Assert — default must be enabled per design spec
        cron.Enabled.ShouldBeTrue();
    }

    [Fact]
    public void CronConfig_WhenEnabledSetFalse_ReturnsDisabled()
    {
        // Arrange & Act
        var cron = new CronConfig { Enabled = false };

        // Assert
        cron.Enabled.ShouldBeFalse();
    }

    // -------------------------------------------------------------------------
    // Issue #12: ExtractAgentDefaults (scenario 2 — loader side)
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractAgentDefaults_WithDefaultsInJson_PopulatesAgentDefaultsAndStripsKey()
    {
        // Arrange
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["defaults"] = new() { Provider = "copilot", Model = "gpt-4.1" }, // would be present after deserialization
                ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1" }
            }
        };
        var rawJson = """
            {
              "agents": {
                "defaults": { "memory": { "enabled": true, "indexing": "auto" } },
                "assistant": { "provider": "copilot", "model": "gpt-4.1", "enabled": true }
              }
            }
            """;

        // Act
        PlatformConfigLoader.ExtractAgentDefaults(config, rawJson);

        // Assert — defaults extracted
        config.AgentDefaults.ShouldNotBeNull();
        config.AgentDefaults!.Memory.ShouldNotBeNull();
        config.AgentDefaults.Memory!.Enabled.ShouldBeTrue();
        config.AgentDefaults.Memory.Indexing.ShouldBe("auto");

        // Assert — reserved key stripped from Agents dictionary
        config.Agents.ShouldNotContainKey("defaults");
        config.Agents!.ShouldContainKey("assistant");
    }

    [Fact]
    public void ExtractAgentDefaults_WithNoDefaultsInJson_LeavesConfigUnchanged()
    {
        // Arrange
        var config = new PlatformConfig
        {
            Agents = new Dictionary<string, AgentDefinitionConfig>
            {
                ["assistant"] = new() { Provider = "copilot", Model = "gpt-4.1" }
            }
        };
        var rawJson = """
            {
              "agents": {
                "assistant": { "provider": "copilot", "model": "gpt-4.1", "enabled": true }
              }
            }
            """;

        // Act
        PlatformConfigLoader.ExtractAgentDefaults(config, rawJson);

        // Assert — no defaults extracted; agents unchanged
        config.AgentDefaults.ShouldBeNull();
        config.Agents!.ShouldContainKey("assistant");
    }

    private sealed class StubLocationResolver : ILocationResolver
    {
        public Location? Resolve(string locationName) => null;
        public string? ResolvePath(string locationName) => null;
        public IReadOnlyList<Location> GetAll() => [];
    }
}
