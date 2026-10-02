using System.IO.Abstractions;
using System.Text.Json.Nodes;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Providers;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Agents;
using BotNexus.Gateway.Configuration;
using BotNexus.Gateway.Configuration.Store;
using BotNexus.Gateway.Configuration.Writers;
using BotNexus.Gateway.Providers;
using BotNexus.Gateway.Tests;
using BotNexus.Gateway.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests.Providers;

/// <summary>
/// Source-backed proof of the running provider activation boundary. The gateway-side options,
/// reconciler, health check and create-agent preflight share one live model registry; provider
/// configuration is written through the canonical SQLite writer with no JSON configuration source.
/// </summary>
public sealed class SqliteProviderAddLiveAssignmentTests
{
    private sealed class TestTimeProvider : TimeProvider
    {
        private readonly List<TestTimer> _timers = [];

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new TestTimer(callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            foreach (var timer in _timers.ToArray())
                timer.Advance(elapsed);
        }

        private sealed class TestTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private TimeSpan _remaining = dueTime;
            private bool _disposed;

            public void Advance(TimeSpan elapsed)
            {
                if (_disposed)
                    return;

                _remaining -= elapsed;
                while (_remaining <= TimeSpan.Zero && !_disposed)
                {
                    callback(state);
                    if (period == Timeout.InfiniteTimeSpan)
                    {
                        _disposed = true;
                        return;
                    }

                    _remaining += period;
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    [Fact]
    public async Task CanonicalSqliteProviderAdd_ActivatesCredentialAndAllowsImmediateAgentAssignment()
    {
        var homePath = Path.Combine(Path.GetTempPath(), $"botnexus-provider-add-{Guid.NewGuid():N}");
        Directory.CreateDirectory(homePath);
        var configPath = Path.Combine(homePath, "config.json");
        var storePath = Path.Combine(homePath, ConfigStoreBootstrap.StoreFileName);
        var fileSystem = new FileSystem();

        try
        {
            var readerStore = new SqliteConfigStore($"Data Source={storePath}");
            var writerStore = new SqliteConfigStore($"Data Source={storePath}");
            await writerStore.WriteDocumentAsync(new JsonObject());

            var time = new TestTimeProvider();
            using var sqliteProvider = new SqliteConfigurationProvider(
                readerStore,
                detectionInterval: TimeSpan.FromSeconds(1),
                timeProvider: time);
            sqliteProvider.Load();
            using var configuration = new ConfigurationRoot([sqliteProvider]);

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddOptions<PlatformConfig>().Bind(configuration);
            services.AddSingleton<IPostConfigureOptions<PlatformConfig>>(
                new PlatformConfigPostConfigure(configuration));
            using var serviceProvider = services.BuildServiceProvider();

            var options = serviceProvider.GetRequiredService<IOptionsMonitor<PlatformConfig>>();
            var modelRegistry = new ModelRegistry();
            // The running gateway always has built-in models. An entirely empty registry permits
            // bootstrap-time assignments, so this fixture must model the actual live preflight.
            modelRegistry.Register("built-in", new LlmModel(
                "stable-model", "Stable model", "integration-mock", "built-in", string.Empty,
                false, ["text"], new ModelCost(0, 0, 0, 0), 128_000, 32_000));
            using var reconciler = new ConfigDefinedModelRegistryReconciler(
                options,
                modelRegistry,
                NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);
            await reconciler.StartAsync(CancellationToken.None);

            var agentRegistry = new DefaultAgentRegistry(NullLogger<DefaultAgentRegistry>.Instance);
            var agentWriter = new Mock<IAgentConfigurationWriter>();
            AgentDescriptor? savedDescriptor = null;
            agentWriter.Setup(writer => writer.SaveAsync(It.IsAny<AgentDescriptor>(), It.IsAny<CancellationToken>()))
                .Callback<AgentDescriptor, CancellationToken>((descriptor, _) => savedDescriptor = descriptor)
                .Returns(Task.CompletedTask);
            var createAgent = new CreateAgentTool(
                agentRegistry,
                agentWriter.Object,
                Array.Empty<IAgentChangeNotifier>(),
                new BotNexusHome(fileSystem, homePath),
                modelRegistry: modelRegistry);
            var authManager = new GatewayAuthManager(options, NullLogger<GatewayAuthManager>.Instance, fileSystem);
            var healthCheck = new DefaultProviderHealthCheck(
                modelRegistry,
                authManager,
                NullLogger<DefaultProviderHealthCheck>.Instance);

            const string providerName = "isolated-foundry";
            const string modelId = "integration-model-4195";
            const string agentId = "provider-added-agent";

            modelRegistry.GetModel(providerName, modelId).ShouldBeNull();
            var beforeAdd = await healthCheck.CheckAsync(providerName);
            beforeAdd.Status.ShouldBe(ProviderHealthStatus.Unhealthy);
            beforeAdd.ModelCount.ShouldBe(0);

            var beforeAssignment = await CreateAgentAsync(createAgent, agentId, providerName, modelId);
            beforeAssignment.Content[0].Value.ShouldContain("Unknown API provider");
            agentRegistry.Contains(BotNexus.Domain.Primitives.AgentId.From(agentId)).ShouldBeFalse();

            var changed = new TaskCompletionSource<PlatformConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = options.OnChange((config, _) =>
            {
                if (config.Providers?.ContainsKey(providerName) == true)
                    changed.TrySetResult(config);
            });

            var sqliteWriter = new SqliteConfigurationWriter(writerStore, storePath);
            var supportedWriter = new PlatformConfigWriter(
                configPath,
                fileSystem,
                backup: null,
                writer: sqliteWriter,
                pristineStore: writerStore);
            await supportedWriter.UpdateSectionEntryAsync(
                "providers",
                providerName,
                JsonNode.Parse("""
                    {
                      "enabled": true,
                      "apiKey": "integration-test-api-key",
                      "baseUrl": "https://provider.example.invalid/v1",
                      "chat": {
                        "api": "openai-responses",
                        "models": ["integration-model-4195"]
                      }
                    }
                    """)!);

            time.Advance(TimeSpan.FromSeconds(1));
            var observedConfig = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            observedConfig.Providers.ShouldNotBeNull();
            observedConfig.Providers.ShouldContainKey(providerName);
            File.Exists(configPath).ShouldBeFalse("SQLite is the only configuration backend in this fixture");

            var activatedModel = modelRegistry.GetModel(providerName, modelId);
            activatedModel.ShouldNotBeNull();
            activatedModel.Api.ShouldBe("openai-responses");
            activatedModel.BaseUrl.ShouldBe("https://provider.example.invalid/v1");

            (await authManager.GetApiKeyAsync(providerName)).ShouldBe("integration-test-api-key");
            var afterAdd = await healthCheck.CheckAsync(providerName);
            afterAdd.Status.ShouldBe(ProviderHealthStatus.Healthy);
            afterAdd.ModelCount.ShouldBe(1);
            afterAdd.HasCredentials.ShouldBeTrue();

            var assignment = await CreateAgentAsync(createAgent, agentId, providerName, modelId);
            assignment.Content[0].Value.ShouldContain("created");
            agentRegistry.Contains(BotNexus.Domain.Primitives.AgentId.From(agentId)).ShouldBeTrue();
            savedDescriptor.ShouldNotBeNull();
            savedDescriptor.ApiProvider.ShouldBe(providerName);
            savedDescriptor.ModelId.ShouldBe(modelId);
            agentWriter.Verify(
                writer => writer.SaveAsync(
                    It.Is<AgentDescriptor>(descriptor => descriptor.ApiProvider == providerName && descriptor.ModelId == modelId),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }
        finally
        {
            SqlitePoolCleanup.ClearPoolFor(storePath);
            if (Directory.Exists(homePath))
                Directory.Delete(homePath, recursive: true);
        }
    }

    private static Task<BotNexus.Agent.Core.Types.AgentToolResult> CreateAgentAsync(
        CreateAgentTool tool,
        string agentId,
        string provider,
        string model) => tool.ExecuteAsync(
            "integration-test",
            new Dictionary<string, object?>
            {
                ["id"] = agentId,
                ["displayName"] = "Provider readiness integration agent",
                ["modelId"] = model,
                ["apiProvider"] = provider
            });
}
