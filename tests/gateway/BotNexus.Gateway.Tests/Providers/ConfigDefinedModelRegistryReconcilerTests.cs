using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

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
