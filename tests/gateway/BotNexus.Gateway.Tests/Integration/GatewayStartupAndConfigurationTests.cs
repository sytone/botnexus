using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Gateway;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Copilot.Messages;
using BotNexus.Agent.Providers.Core;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Agent.Providers.OpenAI;
using BotNexus.Agent.Providers.OpenAICompat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Integration;

[Trait("Category", "Integration")]
[Collection("IntegrationTests")]
public sealed class GatewayStartupAndConfigurationTests
{
    private const string ConfigPathKey = "BotNexus__ConfigPath";
    private static readonly SemaphoreSlim EnvLock = new(1, 1);

    [Fact]
    public async Task GatewayStartup_WithValidDefaultConfig_StartsAndServesHealthAndSwagger()
    {
        using var fixture = new GatewayStartupFixture();
        fixture.WriteDefaultConfig("""
            {
              "defaultAgentId": "startup-agent",
              "providers": {
                "github-copilot": {
                  "apiKey": "auth:copilot",
                  "baseUrl": "https://api.githubcopilot.com",
                  "defaultModel": "gpt-4.1"
                }
              }
            }
            """);

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var health = await client.GetFromJsonAsync<JsonElement>("/health");
            var swagger = await client.GetAsync("/swagger");
            var swaggerJson = await client.GetAsync("/swagger/v1/swagger.json");

            health.GetProperty("status").GetString().ShouldBe("ok");
            swagger.StatusCode.ShouldBe(HttpStatusCode.OK);
            swaggerJson.StatusCode.ShouldBe(HttpStatusCode.OK);
        });
    }

    [Fact]
    public async Task GatewayStartup_WhenConfigMissing_StaysHealthyAndValidationEndpointReportsMissingFile()
    {
        using var fixture = new GatewayStartupFixture();

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var health = await client.GetFromJsonAsync<JsonElement>("/health");
            var validationResponse = await client.GetFromJsonAsync<JsonElement>("/api/config/validate");

            health.GetProperty("status").GetString().ShouldBe("ok");
            validationResponse.GetProperty("isValid").GetBoolean().ShouldBeFalse();
            var firstError = validationResponse.GetProperty("errors")[0].GetString();
            firstError.ShouldNotBeNull();
            var errorText = firstError ?? throw new InvalidOperationException("Expected validation error text.");
            errorText.ShouldContain("Config file not found");
        });
    }

    [Fact]
    public async Task GatewayConfiguration_LoadsProviderConfigFromConfigJson()
    {
        using var fixture = new GatewayStartupFixture();
        var configPath = fixture.WriteConfig("providers.json", """
            {
              "providers": {
                "github-copilot": {
                  "apiKey": "auth:copilot",
                  "baseUrl": "https://api.githubcopilot.com",
                  "defaultModel": "gpt-4.1"
                }
              }
            }
            """);

        await fixture.WithEnvironmentAsync(() =>
        {
            var config = PlatformConfigLoader.Load(configPath, validateOnLoad: false);

            config.Providers.ShouldNotBeNull();
            var providers = config.Providers ?? throw new InvalidOperationException("Expected providers.");
            providers.ShouldContainKey("github-copilot");
            providers["github-copilot"].ApiKey.ShouldBe("auth:copilot");
            providers["github-copilot"].BaseUrl.ShouldBe("https://api.githubcopilot.com");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task GatewayConfiguration_LayersEnvironmentConfigPathOverAppSettingsConfigPath()
    {
        using var fixture = new GatewayStartupFixture();
        var appSettingsConfigPath = fixture.WriteConfig("appsettings.json", """{"defaultAgentId":"from-appsettings"}""");
        var envConfigPath = fixture.WriteConfig("env.json", """{"defaultAgentId":"from-env"}""");

        await fixture.WithEnvironmentAsync(() =>
        {
            Environment.SetEnvironmentVariable(ConfigPathKey, envConfigPath);
            try
            {
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["BotNexus:ConfigPath"] = appSettingsConfigPath
                    })
                    .AddEnvironmentVariables()
                    .Build();

                var resolvedConfigPath = configuration["BotNexus:ConfigPath"];
                var config = PlatformConfigLoader.Load(resolvedConfigPath, validateOnLoad: false);
                config.Gateway?.DefaultAgentId.ShouldBe("from-env");
                return Task.CompletedTask;
            }
            finally
            {
                Environment.SetEnvironmentVariable(ConfigPathKey, null);
            }
        });
    }

    [Fact]
    public async Task GatewayConfiguration_LayersAppSettingsConfigPathOverDefaultConfigPath()
    {
        using var fixture = new GatewayStartupFixture();
        fixture.WriteDefaultConfig("""{"defaultAgentId":"from-default"}""");
        var appSettingsConfigPath = fixture.WriteConfig("appsettings.json", """{"defaultAgentId":"from-appsettings"}""");

        await fixture.WithEnvironmentAsync(() =>
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["BotNexus:ConfigPath"] = appSettingsConfigPath
                })
                .AddEnvironmentVariables()
                .Build();

            var resolvedConfigPath = configuration["BotNexus:ConfigPath"];
            var config = PlatformConfigLoader.Load(resolvedConfigPath, validateOnLoad: false);
            config.Gateway?.DefaultAgentId.ShouldBe("from-appsettings");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task GatewayApi_WorldEndpoint_ReturnsConfiguredWorldDescriptor()
    {
        using var fixture = new GatewayStartupFixture();
        fixture.WriteDefaultConfig("""
            {
              "gateway": {
                "world": {
                  "id": "local-dev",
                  "name": "Local Development",
                  "description": "Local development gateway",
                  "emoji": "🏠"
                }
              }
            }
            """);

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var world = await client.GetFromJsonAsync<JsonElement>("/api/world");

            world.GetProperty("identity").GetProperty("id").GetString().ShouldBe("local-dev");
            world.GetProperty("identity").GetProperty("name").GetString().ShouldBe("Local Development");
            world.GetProperty("identity").GetProperty("description").GetString().ShouldBe("Local development gateway");
            world.GetProperty("identity").GetProperty("emoji").GetString().ShouldBe("🏠");
            world.GetProperty("hostedAgents").ValueKind.ShouldBe(JsonValueKind.Array);
            world.GetProperty("locations").ValueKind.ShouldBe(JsonValueKind.Array);
            world.GetProperty("availableStrategies").ValueKind.ShouldBe(JsonValueKind.Array);
            world.GetProperty("crossWorldPermissions").ValueKind.ShouldBe(JsonValueKind.Array);
        });
    }

    [Fact]
    public async Task GatewayApi_WorldEndpoint_WhenNotConfigured_ReturnsDefaults()
    {
        using var fixture = new GatewayStartupFixture();

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var world = await client.GetFromJsonAsync<JsonElement>("/api/world");

            world.GetProperty("identity").GetProperty("id").GetString().ShouldBe(Environment.MachineName);
            world.GetProperty("identity").GetProperty("name").GetString().ShouldBe("BotNexus Gateway");
        });
    }

    [Fact]
    public async Task GatewayConfiguration_UsesDefaultConfigPathWhenNoOverridesAreSet()
    {
        using var fixture = new GatewayStartupFixture();
        fixture.WriteDefaultConfig("""{"defaultAgentId":"from-default"}""");

        await fixture.WithEnvironmentAsync(() =>
        {
            Environment.SetEnvironmentVariable(ConfigPathKey, null);
            var config = PlatformConfigLoader.Load(validateOnLoad: false);
            config.Gateway?.DefaultAgentId.ShouldBe("from-default");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void GatewayConfiguration_GithubCopilotModelsMapToRegisteredApiProviders()
    {
        using var httpClient = new HttpClient();

        var apiProviders = new ApiProviderRegistry();
        var models = new ModelRegistry();

        apiProviders.Register(new AnthropicProvider(httpClient));
        apiProviders.Register(new OpenAICompletionsProvider(httpClient, NullLogger<OpenAICompletionsProvider>.Instance));
        apiProviders.Register(new OpenAIResponsesProvider(httpClient, NullLogger<OpenAIResponsesProvider>.Instance));
        apiProviders.Register(new OpenAICompatProvider(httpClient));

        new BuiltInModels().RegisterAll(models);

        var llmClient = new LlmClient(apiProviders, models);
        var registeredApis = llmClient.ApiProviders.GetAll().Select(provider => provider.Api).ToArray();

        registeredApis.ShouldContain("anthropic-messages");
        registeredApis.ShouldContain("openai-completions");
        registeredApis.ShouldContain("openai-responses");
        registeredApis.ShouldContain("openai-compat");
        registeredApis.ShouldNotContain("github-copilot");

        var copilotModel = llmClient.Models.GetModel("github-copilot", "gpt-4.1");
        copilotModel.ShouldNotBeNull();
        registeredApis.ShouldContain(copilotModel!.Api);
    }

    [Fact]
    public void GatewayConfiguration_CopilotMessagesProvider_IsRoutedFromBuiltInClaudeModels()
    {
        // Phase 1b (#810): BuiltInModels now routes the Copilot Claude entries through the
        // carved-out CopilotMessagesProvider (Api="github-copilot-messages"). Both providers
        // remain registered so direct-Anthropic users are unaffected; the user-visible wire
        // contract for Copilot Claude requests is pinned by Phase 0a + the Phase 1a parity
        // tests, which both stayed green across this flip.
        using var httpClient = new HttpClient();

        var apiProviders = new ApiProviderRegistry();
        var models = new ModelRegistry();

        apiProviders.Register(new AnthropicProvider(httpClient));
        apiProviders.Register(new CopilotMessagesProvider(httpClient));
        apiProviders.Register(new OpenAICompletionsProvider(httpClient, NullLogger<OpenAICompletionsProvider>.Instance));
        apiProviders.Register(new OpenAIResponsesProvider(httpClient, NullLogger<OpenAIResponsesProvider>.Instance));
        apiProviders.Register(new OpenAICompatProvider(httpClient));

        new BuiltInModels().RegisterAll(models);

        var llmClient = new LlmClient(apiProviders, models);
        var registeredApis = llmClient.ApiProviders.GetAll().Select(provider => provider.Api).ToArray();

        registeredApis.ShouldContain("anthropic-messages",
            "AnthropicProvider must remain registered — direct-Anthropic users depend on it.");
        registeredApis.ShouldContain("github-copilot-messages",
            "CopilotMessagesProvider must remain registered so the Copilot Claude entries resolve.");

        // After the Phase 1b flip, every Claude entry under github-copilot routes via
        // github-copilot-messages. Spot-check one of each family.
        foreach (var modelId in new[] { "claude-haiku-4.5", "claude-opus-4.6", "claude-sonnet-4.6" })
        {
            var model = llmClient.Models.GetModel("github-copilot", modelId);
            model.ShouldNotBeNull($"BuiltInModels must register github-copilot/{modelId}");
            model!.Api.ShouldBe("github-copilot-messages",
                $"Phase 1b — github-copilot/{modelId} must route via the carved-out provider.");
        }
    }

    [Fact]
    public async Task ConfigEndpoint_ReturnsCronFields_WhenConfigured()
    {
        using var fixture = new GatewayStartupFixture();
        fixture.WriteDefaultConfig("""
            {
              "cron": {
                "enabled": true,
                "tickIntervalSeconds": 30
              }
            }
            """);

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<JsonElement>("/api/config");

            response.TryGetProperty("cron", out var cronProp).ShouldBeTrue("cron section should be present");
            cronProp.TryGetProperty("enabled", out var enabledProp).ShouldBeTrue("cron.enabled should be present");
            enabledProp.GetBoolean().ShouldBeTrue("cron.enabled should be true");
            cronProp.TryGetProperty("tickIntervalSeconds", out var tickProp).ShouldBeTrue("cron.tickIntervalSeconds should be present");
            tickProp.GetInt32().ShouldBe(30, "cron.tickIntervalSeconds should be 30");
        });
    }

    [Fact]
    public async Task ConfigEndpoint_ReturnsEffectiveCronDefaults_WhenCronSectionMissing()
    {
        using var fixture = new GatewayStartupFixture();
        fixture.WriteDefaultConfig("""{}""");

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<JsonElement>("/api/config");

            response.TryGetProperty("cron", out var cronProp).ShouldBeTrue("cron section should be present");
            cronProp.TryGetProperty("enabled", out var enabledProp).ShouldBeTrue("cron.enabled should be present");
            enabledProp.GetBoolean().ShouldBeTrue("cron.enabled should default to true");
            cronProp.TryGetProperty("tickIntervalSeconds", out var tickProp).ShouldBeTrue("cron.tickIntervalSeconds should be present");
            tickProp.GetInt32().ShouldBe(60, "cron.tickIntervalSeconds should default to 60");
        });
    }

    [Fact]
    public async Task ConfigRawEndpoint_OmitsImplicitCronDefaults_WhenCronSectionMissing()
    {
        using var fixture = new GatewayStartupFixture();
        fixture.WriteDefaultConfig("""{}""");

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();

            var response = await client.GetFromJsonAsync<JsonElement>("/api/config/raw");

            response.TryGetProperty("cron", out _).ShouldBeFalse("raw config should not include implicit defaults");
        });
    }

    [Fact]
    public async Task ConfigEndpoint_RedactsSensitiveValues_InEffectiveAndRawResponses()
    {
        using var fixture = new GatewayStartupFixture();
        fixture.WriteDefaultConfig("""
            {
              "apiKey": "root-secret",
              "providers": {
                "github-copilot": {
                  "apiKey": "provider-secret",
                  "baseUrl": "https://api.githubcopilot.com"
                }
              },
              "gateway": {
                "apiKeys": {
                  "tenant-a": {
                    "apiKey": "tenant-secret",
                    "tenantId": "tenant-a",
                    "permissions": [ "chat:send" ]
                  }
                },
                "sessionStore": {
                  "type": "Sqlite",
                  "connectionString": "Data Source=sessions.db"
                },
                "locations": {
                  "db-main": {
                    "type": "database",
                    "connectionString": "Server=tcp:sql.internal;Password=SuperSecret!",
                    "description": "primary"
                  }
                },
                "crossWorld": {
                  "peers": {
                    "world-b": {
                      "endpoint": "https://world-b.example.com",
                      "apiKey": "peer-secret"
                    }
                  },
                  "inbound": {
                    "enabled": true,
                    "allowedWorlds": [ "world-b" ],
                    "apiKeys": {
                      "world-b": "inbound-secret"
                    }
                  }
                }
              }
            }
            """);

        await fixture.WithEnvironmentAsync(async () =>
        {
            await using var factory = CreateTestFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", "tenant-secret");

            var effective = await client.GetFromJsonAsync<JsonElement>("/api/config");
            var raw = await client.GetFromJsonAsync<JsonElement>("/api/config/raw");

            AssertConfigSecretsRedacted(effective);
            AssertConfigSecretsRedacted(raw);
        });
    }

    private static void AssertConfigSecretsRedacted(JsonElement response)
    {
        response.GetProperty("apiKey").GetString().ShouldBe("***");
        response.GetProperty("providers").GetProperty("github-copilot").GetProperty("apiKey").GetString().ShouldBe("***");
        response.GetProperty("gateway").GetProperty("apiKeys").GetProperty("tenant-a").GetProperty("apiKey").GetString().ShouldBe("***");
        response.GetProperty("gateway").GetProperty("sessionStore").GetProperty("connectionString").GetString().ShouldBe("***");
        response.GetProperty("gateway").GetProperty("locations").GetProperty("db-main").GetProperty("connectionString").GetString().ShouldBe("***");
        response.GetProperty("gateway").GetProperty("crossWorld").GetProperty("peers").GetProperty("world-b").GetProperty("apiKey").GetString().ShouldBe("***");
        response.GetProperty("gateway").GetProperty("crossWorld").GetProperty("inbound").GetProperty("apiKeys").GetProperty("world-b").GetString().ShouldBe("***");
    }

    private static WebApplicationFactory<Program> CreateTestFactory(string? appSettingsConfigPath = null)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseUrls("http://127.0.0.1:0");
                builder.ConfigureServices(services =>
                {
                    var hostedServicesToRemove = services
                        .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
                        .ToList();

                    foreach (var descriptor in hostedServicesToRemove)
                        services.Remove(descriptor);
                });

                if (!string.IsNullOrWhiteSpace(appSettingsConfigPath))
                {
                    builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                    {
                        configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["BotNexus:ConfigPath"] = appSettingsConfigPath
                        });
                    });
                }
            });

    private sealed class GatewayStartupFixture : IDisposable
    {
        private readonly string _rootPath = Path.Combine(Path.GetTempPath(), "botnexus-gateway-startup-tests", Guid.NewGuid().ToString("N"));
        private readonly string _homeOverrideBefore = Environment.GetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar) ?? string.Empty;
        private readonly string _configPathOverrideBefore = Environment.GetEnvironmentVariable(ConfigPathKey) ?? string.Empty;

        public GatewayStartupFixture()
        {
            Directory.CreateDirectory(_rootPath);
        }

        public void WriteDefaultConfig(string json)
            => File.WriteAllText(Path.Combine(_rootPath, "config.json"), json);

        public string WriteConfig(string fileName, string json)
        {
            var path = Path.Combine(_rootPath, fileName);
            File.WriteAllText(path, json);
            return path;
        }

        public async Task WithEnvironmentAsync(Func<Task> action)
        {
            await EnvLock.WaitAsync();
            try
            {
                Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, _rootPath);
                Environment.SetEnvironmentVariable(ConfigPathKey, null);
                await action();
            }
            finally
            {
                Environment.SetEnvironmentVariable(BotNexusHome.HomeOverrideEnvVar, string.IsNullOrWhiteSpace(_homeOverrideBefore) ? null : _homeOverrideBefore);
                Environment.SetEnvironmentVariable(ConfigPathKey, string.IsNullOrWhiteSpace(_configPathOverrideBefore) ? null : _configPathOverrideBefore);
                EnvLock.Release();
            }
        }

        public void Dispose()
        {
            if (!Directory.Exists(_rootPath))
                return;

            // Retry with backoff — the test server's bootstrap log may still be locked.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(_rootPath, recursive: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 4)
                        return; // Best-effort: leave temp dir for OS cleanup.

                    Thread.Sleep(500 * (attempt + 1));
                }
            }
        }
    }
}
