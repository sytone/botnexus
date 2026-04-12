using System.Diagnostics;
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
        config.Version.Should().Be(1);
        config.Gateway.Should().BeNull();
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

        config.Gateway?.ListenUrl.Should().Be("http://localhost:18790");
        config.Gateway?.DefaultAgentId.Should().Be("agent-a");
        config.Gateway?.LogLevel.Should().Be("Debug");
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

        config.Gateway?.DefaultAgentId.Should().Be("custom-agent");
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

        errors.Should().Contain(e => e.Contains("listenUrl", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("logLevel", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("agentsDirectory", StringComparison.Ordinal));
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

        errors.Should().ContainSingle(e => e.Contains("listenUrl", StringComparison.Ordinal));
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

        errors.Should().ContainSingle(e => e.Contains("logLevel", StringComparison.Ordinal));
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

        errors.Should().NotContain(e => e.Contains("providers.copilot must define apiKey or baseUrl", StringComparison.Ordinal),
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

        errors.Should().ContainSingle(e => e.Contains("providers.copilot.baseUrl", StringComparison.Ordinal));
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

        errors.Should().Contain(e => e.Contains("gateway.crossWorld.peers.world-b.endpoint", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("gateway.crossWorld.inbound.apiKeys.world-b", StringComparison.Ordinal));
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

        config.Version.Should().Be(1);
        config.ApiKey.Should().BeNull();
        config.Gateway.Should().BeNull();
        config.Agents.Should().BeNull();
        config.Channels.Should().BeNull();
        config.Cron.Should().BeNull();
        config.Providers.Should().BeNull();
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithMissingVersion_DefaultsToVersionOne()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "missing-version.json");
        await File.WriteAllTextAsync(configPath, """{"defaultAgentId":"assistant"}""");

        var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);

        config.Version.Should().Be(1);
    }

    [Fact]
    public void PlatformConfigLoader_Load_WithMissingVersion_DefaultsToVersionOne()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "missing-version-sync.json");
        File.WriteAllText(configPath, """{"defaultAgentId":"assistant"}""");

        var config = PlatformConfigLoader.Load(configPath, validateOnLoad: false);

        config.Version.Should().Be(1);
    }

    [Fact]
    public void PlatformConfigLoader_ValidateWarnings_WithUnknownVersion_ReturnsWarning()
    {
        var warnings = PlatformConfigLoader.ValidateWarnings(new PlatformConfig { Version = 2 });

        warnings.Should().ContainSingle(warning => warning.Contains("version '2'", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformConfigLoader_ValidateWarnings_WithKnownVersion_ReturnsNoWarnings()
    {
        var warnings = PlatformConfigLoader.ValidateWarnings(new PlatformConfig { Version = 1 });

        warnings.Should().BeEmpty();
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

        listener.Messages.Should().Contain(message => message.Contains("Platform config warning", StringComparison.Ordinal));
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

        listener.Messages.Should().NotContain(message => message.Contains("Platform config warning", StringComparison.Ordinal));
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
        var configurationWriter = provider.GetRequiredService<IAgentConfigurationWriter>();

        gatewayOptions.DefaultAgentId.Should().Be("config-agent");
        sessionStore.Should().BeOfType<FileSessionStore>();
        agentSources.Should().Contain(source => source is FileAgentConfigurationSource);
        agentSources.Should().Contain(source => source is PlatformConfigAgentSource);
        configurationWriter.Should().BeOfType<PlatformConfigAgentWriter>();
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

        errors.Should().ContainSingle(e => e.Contains("gateway.sessionStore.type", StringComparison.Ordinal));
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

        errors.Should().ContainSingle(e => e.Contains("gateway.sessionStore.filePath", StringComparison.Ordinal));
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

        errors.Should().ContainSingle(e => e.Contains("gateway.sessionStore.connectionString", StringComparison.Ordinal));
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

        errors.Should().Contain(e => e.Contains("cron.tickIntervalSeconds", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("cron.jobs.job-1.schedule", StringComparison.Ordinal));
        errors.Should().Contain(e => e.Contains("cron.jobs.job-1.actionType", StringComparison.Ordinal));
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
        sessionStore.Should().BeOfType<InMemorySessionStore>();
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
        sessionStore.Should().BeOfType<FileSessionStore>();
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
        sessionStore.Should().BeOfType<SqliteSessionStore>();
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
            sessionStore.Should().BeOfType<InMemorySessionStore>();
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

        compaction.PreservedTurns.Should().Be(5);
        compaction.MaxSummaryChars.Should().Be(2000);
        compaction.TokenThresholdRatio.Should().Be(0.4);
        compaction.ContextWindowTokens.Should().Be(64000);
        compaction.SummarizationModel.Should().Be("gpt-4.1-mini");
    }

    [Fact]
    public async Task PlatformConfigLoader_LoadAsync_WithMissingOptionalSections_UsesDefaultsAndValidates()
    {
        using var fixture = new PlatformConfigFixture();
        var configPath = Path.Combine(fixture.RootPath, "minimal-config.json");
        await File.WriteAllTextAsync(configPath, """{"gateway":{"listenUrl":"http://localhost:5005"}}""");

        var config = await PlatformConfigLoader.LoadAsync(configPath, validateOnLoad: false);
        var errors = PlatformConfigLoader.Validate(config);

        config.Gateway?.ListenUrl.Should().Be("http://localhost:5005");
        config.Providers.Should().BeNull();
        config.Channels.Should().BeNull();
        config.Agents.Should().BeNull();
        errors.Should().BeEmpty();
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

        config.Providers.Should().NotBeNull().And.BeEmpty();
        config.Channels.Should().NotBeNull().And.BeEmpty();
        config.Agents.Should().NotBeNull().And.BeEmpty();
        errors.Should().BeEmpty();
    }

    [Fact]
    public void PlatformConfigLoader_Watch_CanBeCreatedAndDisposed()
    {
        using var fixture = new PlatformConfigFixture();
        using var watcher = PlatformConfigLoader.Watch(fixture.ConfigPath);

        watcher.Should().NotBeNull();
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

        results.Select(config => config.Gateway?.ListenUrl).Should().OnlyContain(value => value == "http://localhost:5005");
        results.Select(config => config.Gateway?.DefaultAgentId).Should().OnlyContain(value => value == "assistant");
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

        reloaded.Gateway?.LogLevel.Should().Be("Warning");
        reloaded.Providers.Should().ContainKey("copilot");
        reloaded.Providers!["copilot"].ApiKey.Should().Be("updated-key");
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
}
