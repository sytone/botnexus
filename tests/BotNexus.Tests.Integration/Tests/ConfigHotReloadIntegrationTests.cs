using System.Net;
using System.Text.Json;
using BotNexus.Core.Abstractions;
using BotNexus.Core.Configuration;
using BotNexus.Core.Extensions;
using BotNexus.Core.Models;
using BotNexus.Gateway;
using BotNexus.Providers.Base;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BotNexus.Tests.Integration.Tests;

[CollectionDefinition("config-hot-reload-integration", DisableParallelization = true)]
public sealed class ConfigHotReloadIntegrationCollection;

[Collection("config-hot-reload-integration")]
public sealed class ConfigHotReloadIntegrationTests
{
    [Fact]
    public async Task AgentConfigChange_AddingNamedAgent_MakesAgentAvailableWithoutRestart()
    {
        using var scope = new HotReloadScope();
        var config = CreateBaseConfig(scope.WorkspacePath);
        await WriteConfigAsync(scope.ConfigPath, config);

        using var factory = CreateFactory(scope);
        using var diScope = factory.Services.CreateScope();
        var router = diScope.ServiceProvider.GetRequiredService<IAgentRouter>();
        var messageForA = new InboundMessage(
            "test",
            "user",
            "chat-a",
            "hello",
            DateTimeOffset.UtcNow,
            [],
            new Dictionary<string, object> { ["agent"] = "agent-a" });
        router.ResolveTargets(messageForA).Any(r => r.AgentName.Equals("agent-a", StringComparison.OrdinalIgnoreCase)).Should().BeTrue();

        config.Agents.Named["agent-b"] = new AgentConfig { Name = "agent-b", Model = "test-model" };
        await WriteConfigAsync(scope.ConfigPath, config);

        var message = new InboundMessage(
            "test",
            "user",
            "chat",
            "hello",
            DateTimeOffset.UtcNow,
            [],
            new Dictionary<string, object> { ["agent"] = "agent-b" });

        await WaitForAsync(() => router.ResolveTargets(message).Any(r => r.AgentName.Equals("agent-b", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ApiKeyChange_RejectsOldKeyAndAcceptsNewKeyWithoutRestart()
    {
        using var scope = new HotReloadScope();
        var config = CreateBaseConfig(scope.WorkspacePath);
        config.Gateway.ApiKey = "old-key";
        await WriteConfigAsync(scope.ConfigPath, config);

        using var factory = CreateFactory(scope);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", config.Gateway.ApiKey);

        (await GetStatusWithApiKeyAsync(client, "old-key")).Should().Be(HttpStatusCode.OK);

        config.Gateway.ApiKey = "new-key";
        await WriteConfigAsync(scope.ConfigPath, config);

        await WaitForAsync(async () =>
            await GetStatusWithApiKeyAsync(client, "old-key") == HttpStatusCode.Unauthorized &&
            await GetStatusWithApiKeyAsync(client, "new-key") == HttpStatusCode.OK);
    }

    [Fact]
    public async Task CronJobAdded_AppearsOnCronApiWithoutRestart()
    {
        using var scope = new HotReloadScope();
        var config = CreateBaseConfig(scope.WorkspacePath);
        config.Cron.Jobs.Clear();
        await WriteConfigAsync(scope.ConfigPath, config);

        using var factory = CreateFactory(scope);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", config.Gateway.ApiKey);

        (await GetCronJobNamesAsync(client)).Should().NotContain("reload-job");

        config.Cron.Jobs["reload-job"] = new CronJobConfig
        {
            Name = "reload-job",
            Type = "agent",
            Schedule = "0 0 * * *",
            Agent = "agent-a",
            Prompt = "test prompt"
        };
        await WriteConfigAsync(scope.ConfigPath, config);

        await WaitForAsync(async () => (await GetCronJobNamesAsync(client)).Contains("reload-job"));
    }

    [Fact]
    public async Task CronJobRemoved_DisappearsFromCronApiWithoutRestart()
    {
        using var scope = new HotReloadScope();
        var config = CreateBaseConfig(scope.WorkspacePath);
        config.Cron.Jobs["reload-job"] = new CronJobConfig
        {
            Name = "reload-job",
            Type = "agent",
            Schedule = "0 0 * * *",
            Agent = "agent-a",
            Prompt = "test prompt"
        };
        await WriteConfigAsync(scope.ConfigPath, config);

        using var factory = CreateFactory(scope);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", config.Gateway.ApiKey);

        (await GetCronJobNamesAsync(client)).Should().Contain("reload-job");

        config.Cron.Jobs.Clear();
        await WriteConfigAsync(scope.ConfigPath, config);

        await WaitForAsync(async () => !(await GetCronJobNamesAsync(client)).Contains("reload-job"));
    }

    [Fact]
    public async Task ProviderConfigChange_UpdatesProviderRegistryWithoutRestart()
    {
        using var scope = new HotReloadScope();
        var config = CreateBaseConfig(scope.WorkspacePath);
        config.Providers.Clear();
        config.Providers["provider-a"] = new ProviderConfig { Auth = "apikey", ApiKey = "a-key", DefaultModel = "a-model" };
        await WriteConfigAsync(scope.ConfigPath, config);

        using var factory = CreateFactory(scope);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", config.Gateway.ApiKey);

        (await GetProviderNamesAsync(client)).Should().Contain("provider-a").And.NotContain("provider-b");

        config.Providers.Clear();
        config.Providers["provider-b"] = new ProviderConfig { Auth = "apikey", ApiKey = "b-key", DefaultModel = "b-model" };
        await WriteConfigAsync(scope.ConfigPath, config);

        await WaitForAsync(async () =>
        {
            var names = await GetProviderNamesAsync(client);
            return names.Contains("provider-b") && !names.Contains("provider-a");
        });
    }

    [Fact]
    public async Task RestartRequiredChange_HostOrPortUpdate_LogsWarningAndGatewayStaysRunning()
    {
        using var scope = new HotReloadScope();
        var config = CreateBaseConfig(scope.WorkspacePath);
        await WriteConfigAsync(scope.ConfigPath, config);

        using var factory = CreateFactory(scope);
        using var client = factory.CreateClient();
        using var diScope = factory.Services.CreateScope();
        var stream = diScope.ServiceProvider.GetRequiredService<IActivityStream>();
        using var subscription = stream.Subscribe();

        config.Gateway.Port += 1;
        await WriteConfigAsync(scope.ConfigPath, config);

        await WaitForAsync(async () =>
        {
            var response = await client.GetAsync("/health");
            return response.StatusCode == HttpStatusCode.OK;
        });

        var events = await ReadActivityEventsAsync(subscription, TimeSpan.FromSeconds(2));
        events.Any(e => IsConfigReloadEvent(e) && HasReloadAction(e, "restart-required:host-port")).Should().BeTrue();
    }

    [Fact]
    public async Task Debounce_RapidConfigChanges_TriggersSingleReload()
    {
        using var scope = new HotReloadScope();
        var config = CreateBaseConfig(scope.WorkspacePath);
        config.Gateway.ApiKey = "start-key";
        await WriteConfigAsync(scope.ConfigPath, config);

        using var factory = CreateFactory(scope);
        using var client = factory.CreateClient();
        using var diScope = factory.Services.CreateScope();
        var stream = diScope.ServiceProvider.GetRequiredService<IActivityStream>();
        using var subscription = stream.Subscribe();

        config.Gateway.ApiKey = "key-1";
        await WriteConfigAsync(scope.ConfigPath, config);
        await Task.Delay(30);
        config.Gateway.ApiKey = "key-2";
        await WriteConfigAsync(scope.ConfigPath, config);
        await Task.Delay(30);
        config.Gateway.ApiKey = "key-3";
        await WriteConfigAsync(scope.ConfigPath, config);

        await WaitForAsync(async () =>
            await GetStatusWithApiKeyAsync(client, "key-2") == HttpStatusCode.Unauthorized &&
            await GetStatusWithApiKeyAsync(client, "key-3") == HttpStatusCode.OK);

        var events = await ReadActivityEventsAsync(subscription, TimeSpan.FromSeconds(2));
        events.Count(e => IsConfigReloadEvent(e)).Should().Be(1);
    }

    [Fact]
    public async Task InvalidConfig_WritingInvalidJson_LogsErrorAndGatewayStaysRunning()
    {
        using var scope = new HotReloadScope();
        var config = CreateBaseConfig(scope.WorkspacePath);
        config.Gateway.ApiKey = "stable-key";
        await WriteConfigAsync(scope.ConfigPath, config);

        using var factory = CreateFactory(scope);
        using var client = factory.CreateClient();
        using var diScope = factory.Services.CreateScope();
        var stream = diScope.ServiceProvider.GetRequiredService<IActivityStream>();
        using var subscription = stream.Subscribe();

        await File.WriteAllTextAsync(scope.ConfigPath, "{ invalid json");
        await Task.Delay(TimeSpan.FromSeconds(1.2));

        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetStatusWithApiKeyAsync(client, "stable-key")).Should().Be(HttpStatusCode.OK);
        var events = await ReadActivityEventsAsync(subscription, TimeSpan.FromSeconds(1.5));
        events.Any(IsConfigReloadEvent).Should().BeFalse();
    }

    private static WebApplicationFactory<Program> CreateFactory(HotReloadScope scope)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ILlmProvider>();
                    services.RemoveAll<ExtensionServiceRegistration>();

                    services.AddSingleton<ILlmProvider>(new TestLlmProvider("provider-a"));
                    services.AddSingleton<ILlmProvider>(new TestLlmProvider("provider-b"));

                    services.AddSingleton(new ExtensionServiceRegistration(typeof(ILlmProvider), "provider-a"));
                    services.AddSingleton(new ExtensionServiceRegistration(typeof(ILlmProvider), "provider-b"));

                    services.RemoveAll<ProviderRegistry>();
                    services.AddSingleton<ProviderRegistry>(sp =>
                    {
                        var providers = sp.GetServices<ILlmProvider>().ToList();
                        var providerKeys = sp.GetServices<ExtensionServiceRegistration>()
                            .Where(r => r.ServiceType == typeof(ILlmProvider))
                            .Select(r => r.Key)
                            .ToList();
                        var configuredProviderKeys = sp.GetRequiredService<IOptions<BotNexusConfig>>()
                            .Value
                            .Providers
                            .Keys
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);

                        var registry = new ProviderRegistry();
                        var registrationsToApply = Math.Min(providers.Count, providerKeys.Count);
                        for (var i = 0; i < registrationsToApply; i++)
                        {
                            if (configuredProviderKeys.Contains(providerKeys[i]))
                                registry.Register(providerKeys[i], providers[i]);
                        }

                        return registry;
                    });
                });
            });
    }

    private static BotNexusConfig CreateBaseConfig(string workspacePath)
    {
        return new BotNexusConfig
        {
            ExtensionsPath = "~/.botnexus/extensions",
            Agents = new AgentDefaults
            {
                Workspace = workspacePath,
                Model = "test-model",
                MaxTokens = 4096,
                ContextWindowTokens = 32768,
                Temperature = 0.1,
                MaxToolIterations = 20,
                Timezone = "UTC",
                Named = new Dictionary<string, AgentConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["agent-a"] = new AgentConfig { Name = "agent-a", Model = "test-model", Provider = "provider-a" }
                }
            },
            Providers = new ProvidersConfig
            {
                ["provider-a"] = new ProviderConfig { Auth = "apikey", ApiKey = "test-key", DefaultModel = "test-model" }
            },
            Gateway = new GatewayConfig
            {
                Host = "127.0.0.1",
                Port = 18790,
                ApiKey = "test-api-key",
                WebSocketEnabled = true,
                WebSocketPath = "/ws",
                DefaultAgent = "agent-a"
            },
            Cron = new CronConfig
            {
                Enabled = true,
                TickIntervalSeconds = 1,
                ExecutionHistorySize = 100
            }
        };
    }

    private static async Task WriteConfigAsync(string configPath, BotNexusConfig config)
    {
        var payload = new Dictionary<string, BotNexusConfig>
        {
            [BotNexusConfig.SectionName] = config
        };
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static async Task<HashSet<string>> GetProviderNamesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/providers");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.EnumerateArray()
            .Select(static entry => entry.GetProperty("name").GetString())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<HashSet<string>> GetCronJobNamesAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/cron");
        response.EnsureSuccessStatusCode();
        var root = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return root.EnumerateArray()
            .Select(static entry => entry.GetProperty("name").GetString())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<HttpStatusCode> GetStatusWithApiKeyAsync(HttpClient client, string apiKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/channels");
        request.Headers.Add("X-Api-Key", apiKey);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<List<ActivityEvent>> ReadActivityEventsAsync(IActivitySubscription subscription, TimeSpan window)
    {
        using var cts = new CancellationTokenSource(window);
        var events = new List<ActivityEvent>();
        try
        {
            while (true)
                events.Add(await subscription.ReadAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
            return events;
        }
    }

    private static bool IsConfigReloadEvent(ActivityEvent activityEvent)
    {
        if (activityEvent.Metadata is null)
            return false;

        return activityEvent.Metadata.TryGetValue("event", out var value) &&
               value is string eventName &&
               string.Equals(eventName, "gateway.config.reloaded", StringComparison.Ordinal);
    }

    private static bool HasReloadAction(ActivityEvent activityEvent, string action)
    {
        if (activityEvent.Metadata is null)
            return false;

        if (!activityEvent.Metadata.TryGetValue("actions", out var actionsRaw))
            return false;

        return actionsRaw switch
        {
            IEnumerable<string> stringActions => stringActions.Contains(action, StringComparer.OrdinalIgnoreCase),
            IEnumerable<object> objectActions => objectActions
                .Select(static a => a?.ToString())
                .Where(static a => !string.IsNullOrWhiteSpace(a))
                .Contains(action, StringComparer.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 10000, int pollMs = 100)
    {
        var started = DateTimeOffset.UtcNow;
        while (!await condition())
        {
            if ((DateTimeOffset.UtcNow - started).TotalMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(pollMs);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10000, int pollMs = 100)
    {
        var started = DateTimeOffset.UtcNow;
        while (!condition())
        {
            if ((DateTimeOffset.UtcNow - started).TotalMilliseconds > timeoutMs)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(pollMs);
        }
    }

    private sealed class HotReloadScope : IDisposable
    {
        private readonly string? _previousHome;

        public HotReloadScope()
        {
            RootPath = Path.Combine(AppContext.BaseDirectory, "test-artifacts", "config-hot-reload", Guid.NewGuid().ToString("N"));
            HomePath = Path.Combine(RootPath, "home");
            WorkspacePath = Path.Combine(RootPath, "workspace");
            ConfigPath = Path.Combine(HomePath, "config.json");

            Directory.CreateDirectory(HomePath);
            Directory.CreateDirectory(WorkspacePath);

            _previousHome = Environment.GetEnvironmentVariable("BOTNEXUS_HOME");
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", HomePath);
        }

        public string RootPath { get; }
        public string HomePath { get; }
        public string WorkspacePath { get; }
        public string ConfigPath { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("BOTNEXUS_HOME", _previousHome);
            if (Directory.Exists(RootPath))
            {
                try
                {
                    Directory.Delete(RootPath, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    private sealed class TestLlmProvider(string name) : ILlmProvider
    {
        public string DefaultModel => $"{name}-default-model";
        public GenerationSettings Generation { get; set; } = new() { Model = $"{name}-model", MaxTokens = 512, Temperature = 0.1 };

        public Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<string>>(new[] { DefaultModel });
        }

        public Task<LlmResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse($"response:{name}", FinishReason.Stop));

        public async IAsyncEnumerable<StreamingChatChunk> ChatStreamAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await ChatAsync(request, cancellationToken);
            yield return StreamingChatChunk.FromContentDelta(response.Content);
        }
    }
}
