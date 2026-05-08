using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BotNexus.Gateway;
using BotNexus.Gateway.Api;
using BotNexus.Gateway.Configuration;
using BotNexus.Agent.Providers.Anthropic;
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
            validationResponse.GetProperty("errors")[0].GetString().ShouldContain("Config file not found");
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

            config.Providers.ShouldContainKey("github-copilot");
            config.Providers!["github-copilot"].ApiKey.ShouldBe("auth:copilot");
            config.Providers["github-copilot"].BaseUrl.ShouldBe("https://api.githubcopilot.com");
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

