using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Providers;

public sealed class ConfiguredModelCapacityTests
{
    [Fact]
    public async Task Registration_PerModelAndProviderInstance_AreIndependent()
    {
        var chat = new ProviderChatConfig
        {
            Models = ["large", "small", "Large"],
            ContextWindow = 200_000,
            ModelCapacities = new(StringComparer.Ordinal)
            {
                ["large"] = new() { ContextWindow = 1_050_000, MaxTokens = 128_000 },
                ["small"] = new() { ContextWindow = 64_000 }
            }
        };
        var config = Config(chat);
        config.Providers.ShouldNotBeNull();
        config.Providers["other"] = Provider(new ProviderChatConfig { Models = ["large"] });
        var registry = new ModelRegistry();
        using var reconciler = Reconciler(new TestOptionsMonitor<PlatformConfig>(config), registry);
        await reconciler.StartAsync(CancellationToken.None);

        var large = registry.GetModel("dynamic", "large");
        large.ShouldNotBeNull();
        large.ContextWindow.ShouldBe(1_050_000);
        large.MaxTokens.ShouldBe(128_000);
        large.ContextWindowSource.ShouldBe("configured-model");
        large.MaxTokensSource.ShouldBe("configured-model");
        var small = registry.GetModel("dynamic", "small");
        small.ShouldNotBeNull();
        small.ContextWindow.ShouldBe(64_000);
        small.MaxTokens.ShouldBe(32_000);
        small.MaxTokensSource.ShouldBe("fallback");
        var caseVariant = registry.GetModel("dynamic", "Large");
        caseVariant.ShouldNotBeNull();
        caseVariant.ContextWindow.ShouldBe(200_000);
        caseVariant.ContextWindowSource.ShouldBe("configured-provider");
        var other = registry.GetModel("other", "large");
        other.ShouldNotBeNull();
        other.ContextWindow.ShouldBe(128_000);
        other.ContextWindowSource.ShouldBe("fallback");
        other.MaxTokensSource.ShouldBe("fallback");
    }

    [Fact]
    public void CentralConfigCliPath_WholeMapPreservesExactModelIds()
    {
        var config = Config(new ProviderChatConfig());
        var resolver = new ConfigPathResolver();
        resolver.TrySetValue(config, "providers.dynamic.chat.modelCapacities",
            """{"Model.1":{"contextWindow":1050000,"maxTokens":128000},"model.1":{"contextWindow":64000}}""",
            out var error).ShouldBeTrue(error);
        config.Providers.ShouldNotBeNull();
        var provider = config.Providers["dynamic"];
        ConfiguredModelCapacityResolver.Resolve("dynamic", provider, "Model.1").ContextWindow.ShouldBe(1_050_000);
        ConfiguredModelCapacityResolver.Resolve("dynamic", provider, "model.1").ContextWindow.ShouldBe(64_000);
        ConfiguredModelCapacityResolver.Resolve("dynamic", provider, "MODEL.1").ContextWindowSource.ShouldBe("fallback");
    }

    [Fact]
    public void Resolve_PartialOverrideAndLegacyProviderDefault_ResolveEachField()
    {
        var provider = Provider(new ProviderChatConfig
        {
            ModelCapacities = new() { ["model"] = new() { MaxTokens = 48_000 } }
        });
        provider.ContextWindow = 100_000;
        var capacity = ConfiguredModelCapacityResolver.Resolve("dynamic", provider, "model");
        capacity.ContextWindow.ShouldBe(100_000);
        capacity.MaxTokens.ShouldBe(48_000);
        capacity.ContextWindowSource.ShouldBe("configured-provider");
        capacity.MaxTokensSource.ShouldBe("configured-model");
    }

    [Fact]
    public void Resolve_SmallContext_CapsOnlyFallbackOutput()
    {
        var capacity = ConfiguredModelCapacityResolver.Resolve("dynamic",
            Provider(new ProviderChatConfig { ContextWindow = 10_000 }), "model");
        capacity.MaxTokens.ShouldBe(9_999);
        capacity.MaxTokensSource.ShouldBe("fallback");
    }

    [Theory]
    [InlineData(0, 32_000)]
    [InlineData(-1, 32_000)]
    [InlineData(1, 0)]
    [InlineData(128_000, 0)]
    [InlineData(128_000, -1)]
    [InlineData(128_000, 128_000)]
    [InlineData(128_000, 128_001)]
    public async Task Reload_InvalidCapacity_KeepsLastKnownGoodAndNamesFailure(int context, int output)
    {
        var monitor = new TestOptionsMonitor<PlatformConfig>(Config(new ProviderChatConfig { Models = ["model"] }));
        var registry = new ModelRegistry();
        using var reconciler = Reconciler(monitor, registry);
        await reconciler.StartAsync(CancellationToken.None);
        var original = registry.GetModel("dynamic", "model");
        original.ShouldNotBeNull();
        var invalid = Config(new ProviderChatConfig
        {
            Models = ["model"],
            ModelCapacities = new() { ["model"] = new() { ContextWindow = context, MaxTokens = output } }
        });
        Should.Throw<InvalidOperationException>(() => reconciler.BuildRegistrations(invalid))
            .Message.ShouldContain("model");
        monitor.RaiseChanged(invalid);
        registry.GetModel("dynamic", "model").ShouldBe(original);
        var failure = reconciler.GetActivationFailure("dynamic");
        failure.ShouldNotBeNull();
        failure.ShouldContain("dynamic");
        failure.ShouldContain("model");
        monitor.RaiseChanged(Config(new ProviderChatConfig { Models = ["model"], ContextWindow = 200_000 }));
        reconciler.GetActivationFailure("dynamic").ShouldBeNull();
        var recovered = registry.GetModel("dynamic", "model");
        recovered.ShouldNotBeNull();
        recovered.ContextWindow.ShouldBe(200_000);
    }

    [Fact]
    public void ExistingConstructor_ReportsRegisteredRatherThanVerified()
    {
        var model = new LlmModel("id", "name", "api", "provider", "", false, ["text"],
            new ModelCost(0, 0, 0, 0), 128_000, 32_000);
        model.ContextWindowSource.ShouldBe("registered");
        model.MaxTokensSource.ShouldBe("registered");
    }

    private static ProviderConfig Provider(ProviderChatConfig chat) => new()
    {
        Enabled = true, BaseUrl = "https://example.test/v1", Chat = chat
    };

    private static PlatformConfig Config(ProviderChatConfig chat) => new()
    {
        Providers = new() { ["dynamic"] = Provider(chat) }
    };

    private static ConfigDefinedModelRegistryReconciler Reconciler(
        TestOptionsMonitor<PlatformConfig> monitor, ModelRegistry registry) => new(
        monitor, registry, NullLogger<ConfigDefinedModelRegistryReconciler>.Instance);
}
