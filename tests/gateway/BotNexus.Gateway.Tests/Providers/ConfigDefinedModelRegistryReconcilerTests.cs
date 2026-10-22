using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class ConfigDefinedModelRegistryReconcilerTests
{
    [Fact]
    public async Task StartAndReload_AddUpdateDisableAndRemove_ReconcileLiveRegistry()
    {
        var initial = ConfigWithProvider(
            "dynamic",
            enabled: true,
            baseUrl: "https://one.example/v1",
            api: "openai-responses",
            models: ["model-a"]);
        var monitor = new TestOptionsMonitor<PlatformConfig>(initial);
        var registry = new ModelRegistry();
        registry.Register("built-in", Model("stable", "built-in", "integration-mock", string.Empty));
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor,
            registry,
            NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);

        var first = registry.GetModel("dynamic", "model-a");
        first.ShouldNotBeNull();
        first.Api.ShouldBe("openai-responses");
        first.BaseUrl.ShouldBe("https://one.example/v1");

        monitor.RaiseChanged(ConfigWithProvider(
            "dynamic",
            enabled: true,
            baseUrl: "https://two.example/v1",
            api: "openai-completions",
            models: ["model-b"]));

        registry.GetModel("dynamic", "model-a").ShouldBeNull();
        var updated = registry.GetModel("dynamic", "model-b");
        updated.ShouldNotBeNull();
        updated.Api.ShouldBe("openai-completions");
        updated.BaseUrl.ShouldBe("https://two.example/v1");

        monitor.RaiseChanged(ConfigWithProvider(
            "dynamic",
            enabled: false,
            baseUrl: "https://two.example/v1",
            api: "openai-completions",
            models: ["model-b"]));

        registry.GetModel("dynamic", "model-b").ShouldBeNull();
        registry.GetModel("built-in", "stable").ShouldNotBeNull();

        monitor.RaiseChanged(new PlatformConfig());

        registry.GetProviders().ShouldNotContain("dynamic");
        registry.GetModel("built-in", "stable").ShouldNotBeNull();
    }

    [Fact]
    public async Task Start_NamedCopilotProvider_DoesNotProjectGenericConfigModels()
    {
        var config = ConfigWithProvider(
            "work-copilot",
            enabled: true,
            baseUrl: null,
            api: "openai-completions",
            models: ["gpt-5"]);
        config.Providers!["work-copilot"].Type = "github-copilot";
        var monitor = new TestOptionsMonitor<PlatformConfig>(config);
        var registry = new ModelRegistry();
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor,
            registry,
            NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);

        registry.GetProviders().ShouldNotContain("work-copilot");
    }

    [Fact]
    public async Task StartAndReload_UntypedCanonicalCopilot_DoesNotRejectValidResponsesOverlay()
    {
        var initial = ConfigWithProvider(
            "github-copilot",
            enabled: true,
            baseUrl: null,
            api: "openai-completions",
            models: ["copilot-model"]);
        initial.Providers!["foundry"] = new ProviderConfig
        {
            Enabled = true,
            BaseUrl = "https://one.example/v1",
            Chat = new ProviderChatConfig { Api = "openai-responses", Models = ["model-a"] }
        };
        var monitor = new TestOptionsMonitor<PlatformConfig>(initial);
        var registry = new ModelRegistry();
        registry.Register("github-copilot", Model("copilot-model", "github-copilot", "github-copilot", string.Empty));
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor,
            registry,
            NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);

        registry.GetModel("foundry", "model-a").ShouldNotBeNull();
        registry.GetModel("github-copilot", "copilot-model")!.Api.ShouldBe("github-copilot");

        var updated = ConfigWithProvider(
            "github-copilot",
            enabled: true,
            baseUrl: null,
            api: "openai-completions",
            models: ["copilot-model"]);
        updated.Providers!["foundry"] = new ProviderConfig
        {
            Enabled = true,
            BaseUrl = "https://two.example/v1",
            Chat = new ProviderChatConfig { Api = "openai-responses", Models = ["model-b"] }
        };
        monitor.RaiseChanged(updated);

        registry.GetModel("foundry", "model-a").ShouldBeNull();
        registry.GetModel("foundry", "model-b")!.BaseUrl.ShouldBe("https://two.example/v1");
        registry.GetModel("github-copilot", "copilot-model")!.Api.ShouldBe("github-copilot");
    }

    [Fact]
    public async Task Reload_DuplicateModelIdsAcrossProviders_UpdatesOnlyChangedProvider()
    {
        var initial = ConfigWithProvider(
            "provider-a",
            enabled: true,
            baseUrl: "https://a.example/v1",
            api: "openai-responses",
            models: ["shared-model"]);
        initial.Providers.ShouldNotBeNull();
        initial.Providers["provider-b"] = new ProviderConfig
        {
            Enabled = true,
            BaseUrl = "https://b.example/v1",
            Chat = new ProviderChatConfig { Api = "openai-responses", Models = ["shared-model"] }
        };
        var monitor = new TestOptionsMonitor<PlatformConfig>(initial);
        var registry = new ModelRegistry();
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor,
            registry,
            NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);

        var initialA = registry.GetModel("provider-a", "shared-model");
        initialA.ShouldNotBeNull();
        initialA.BaseUrl.ShouldBe("https://a.example/v1");
        var initialB = registry.GetModel("provider-b", "shared-model");
        initialB.ShouldNotBeNull();
        initialB.BaseUrl.ShouldBe("https://b.example/v1");

        var updated = ConfigWithProvider(
            "provider-a",
            enabled: true,
            baseUrl: "https://a-updated.example/v1",
            api: "openai-responses",
            models: ["shared-model"]);
        updated.Providers.ShouldNotBeNull();
        updated.Providers["provider-b"] = initial.Providers["provider-b"];
        monitor.RaiseChanged(updated);

        var updatedA = registry.GetModel("provider-a", "shared-model");
        updatedA.ShouldNotBeNull();
        updatedA.BaseUrl.ShouldBe("https://a-updated.example/v1");
        var unchangedB = registry.GetModel("provider-b", "shared-model");
        unchangedB.ShouldNotBeNull();
        unchangedB.BaseUrl.ShouldBe("https://b.example/v1");
        registry.GetModels("provider-a").Select(model => model.Id).ShouldBe(["shared-model"]);
        registry.GetModels("provider-b").Select(model => model.Id).ShouldBe(["shared-model"]);
    }

    [Fact]
    public async Task Reload_InvalidOpenAiCompletionsProvider_KeepsLastKnownGoodCatalogue()
    {
        var monitor = new TestOptionsMonitor<PlatformConfig>(ConfigWithProvider(
            "dynamic",
            enabled: true,
            baseUrl: "https://one.example/v1",
            api: "openai-completions",
            models: ["model-a"]));
        var registry = new ModelRegistry();
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor,
            registry,
            NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);
        await reconciler.StartAsync(CancellationToken.None);

        monitor.RaiseChanged(ConfigWithProvider(
            "dynamic",
            enabled: true,
            baseUrl: null,
            api: "openai-completions",
            models: ["model-b"]));

        registry.GetModel("dynamic", "model-a").ShouldNotBeNull();
        registry.GetModel("dynamic", "model-b").ShouldBeNull();
        reconciler.GetActivationFailure("dynamic").ShouldBe("Provider 'dynamic' requires a base URL for openai-completions.");
    }

    [Fact]
    public async Task SuccessfulReload_ClearsPreviousActivationFailure()
    {
        var monitor = new TestOptionsMonitor<PlatformConfig>(ConfigWithProvider(
            "dynamic",
            enabled: true,
            baseUrl: null,
            api: "openai-completions",
            models: ["model-a"]));
        var registry = new ModelRegistry();
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor,
            registry,
            NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);
        reconciler.GetActivationFailure("dynamic").ShouldNotBeNull();

        monitor.RaiseChanged(ConfigWithProvider(
            "dynamic",
            enabled: true,
            baseUrl: "https://valid.example/v1",
            api: "openai-completions",
            models: ["model-a"]));

        reconciler.GetActivationFailure("dynamic").ShouldBeNull();
        registry.GetModel("dynamic", "model-a").ShouldNotBeNull();
    }

    [Fact]
    public async Task Reload_UnregisteredApi_KeepsLastKnownGoodCatalogueAndReportsFailure()
    {
        var monitor = new TestOptionsMonitor<PlatformConfig>(ConfigWithProvider(
            "dynamic",
            enabled: true,
            baseUrl: "https://one.example/v1",
            api: "openai-responses",
            models: ["model-a"]));
        var registry = new ModelRegistry();
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor,
            registry,
            ApiProviders("openai-responses"),
            NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);
        await reconciler.StartAsync(CancellationToken.None);

        monitor.RaiseChanged(ConfigWithProvider(
            "dynamic",
            enabled: true,
            baseUrl: "https://two.example/v1",
            api: "unregistered-chat-api",
            models: ["model-b"]));

        registry.GetModel("dynamic", "model-a").ShouldNotBeNull();
        registry.GetModel("dynamic", "model-b").ShouldBeNull();
        reconciler.GetActivationFailure("dynamic").ShouldBe(
            "Provider 'dynamic' uses unregistered chat API 'unregistered-chat-api'.");
    }

    [Fact]
    public async Task Start_ChangeAtSubscriptionBoundary_ProjectsLatestSnapshot()
    {
        var monitor = new BoundaryOptionsMonitor(StartupConfig("old-model"));
        monitor.BeforeSubscribe = () => monitor.Publish(StartupConfig("new-model"));
        var registry = new ModelRegistry();
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor, registry, NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);

        registry.GetModels("dynamic").Select(model => model.Id).ShouldBe(["new-model"]);
    }

    [Fact]
    public async Task Start_ChangeWhileReadingStartupSnapshot_DoesNotOverwriteNewerNotification()
    {
        var monitor = new BoundaryOptionsMonitor(StartupConfig("old-model"));
        // Capture the startup value, then deliver a newer change before returning that old value.
        // This also guards against fixing the gap by only moving OnChange before CurrentValue.
        monitor.AfterSnapshotRead = () => monitor.Publish(StartupConfig("new-model"));
        var registry = new ModelRegistry();
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor, registry, NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);

        await reconciler.StartAsync(CancellationToken.None);

        registry.GetModels("dynamic").Select(model => model.Id).ShouldBe(["new-model"]);
    }

    [Fact]
    public async Task Reload_DelayedOlderNotification_ProjectsCurrentSnapshot()
    {
        var oldConfig = StartupConfig("old-model");
        var monitor = new BoundaryOptionsMonitor(oldConfig);
        var registry = new ModelRegistry();
        using var reconciler = new ConfigDefinedModelRegistryReconciler(
            monitor, registry, NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);
        await reconciler.StartAsync(CancellationToken.None);

        monitor.Publish(StartupConfig("new-model"));
        monitor.Notify(oldConfig);

        registry.GetModels("dynamic").Select(model => model.Id).ShouldBe(["new-model"]);
    }

    private static PlatformConfig StartupConfig(string model) => ConfigWithProvider(
        "dynamic", enabled: true, baseUrl: "https://example.invalid/v1",
        api: "openai-responses", models: [model]);

    private sealed class BoundaryOptionsMonitor(PlatformConfig initial) : IOptionsMonitor<PlatformConfig>
    {
        private PlatformConfig _current = initial;
        private Action<PlatformConfig, string?>? _listener;

        public Action? BeforeSubscribe { get; set; }
        public Action? AfterSnapshotRead { get; set; }

        public PlatformConfig CurrentValue
        {
            get
            {
                var snapshot = _current;
                var boundary = AfterSnapshotRead;
                AfterSnapshotRead = null;
                boundary?.Invoke();
                return snapshot;
            }
        }

        public PlatformConfig Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<PlatformConfig, string?> listener)
        {
            BeforeSubscribe?.Invoke();
            _listener = listener;
            return new Subscription(() => _listener = null);
        }

        public void Publish(PlatformConfig updated)
        {
            _current = updated;
            Notify(updated);
        }

        public void Notify(PlatformConfig updated) => _listener?.Invoke(updated, null);

        private sealed class Subscription(Action unsubscribe) : IDisposable
        {
            public void Dispose() => unsubscribe();
        }
    }

    private static ApiProviderRegistry ApiProviders(params string[] apiNames)
    {
        var registry = new ApiProviderRegistry();
        foreach (var apiName in apiNames)
        {
            var provider = Substitute.For<IApiProvider>();
            provider.Api.Returns(apiName);
            registry.Register(provider);
        }

        return registry;
    }

    private static PlatformConfig ConfigWithProvider(
        string name,
        bool enabled,
        string? baseUrl,
        string api,
        List<string> models) => new()
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                [name] = new()
                {
                    Enabled = enabled,
                    BaseUrl = baseUrl,
                    Chat = new ProviderChatConfig
                    {
                        Api = api,
                        Models = models
                    }
                }
            }
        };

    private static LlmModel Model(string id, string provider, string api, string baseUrl) => new(
        id,
        id,
        api,
        provider,
        baseUrl,
        false,
        ["text"],
        new ModelCost(0, 0, 0, 0),
        128_000,
        32_000);
}
