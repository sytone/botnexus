using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using BotNexus.Gateway.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Tests.Models;

public sealed class ModelDiscoveryServiceTests
{
    private readonly ModelRegistry _registry = new();
    private readonly NullLogger<ModelDiscoveryService> _logger = NullLogger<ModelDiscoveryService>.Instance;

    [Fact]
    public async Task DiscoverAndRegisterAsync_RegistersNewModels()
    {
        // Arrange
        var discoveredModels = new List<LlmModel>
        {
            CreateModel("new-model-1", "New Model 1"),
            CreateModel("new-model-2", "New Model 2")
        };

        var provider = new FakeDiscoveryProvider("test-provider", discoveredModels);
        var service = new ModelDiscoveryService(_registry, [provider], _logger);

        // Act
        await service.DiscoverAndRegisterAsync();

        // Assert
        _registry.GetModel("test-provider", "new-model-1").ShouldNotBeNull();
        _registry.GetModel("test-provider", "new-model-2").ShouldNotBeNull();
    }

    [Fact]
    public async Task DiscoverAndRegisterAsync_OverwritesExistingModels()
    {
        // Arrange: register a built-in model first
        var builtIn = CreateModel("existing-model", "Built-In Name", contextWindow: 64000);
        _registry.Register("test-provider", builtIn);

        var dynamic = CreateModel("existing-model", "Dynamic Name", contextWindow: 200000);
        var provider = new FakeDiscoveryProvider("test-provider", [dynamic]);
        var service = new ModelDiscoveryService(_registry, [provider], _logger);

        // Act
        await service.DiscoverAndRegisterAsync();

        // Assert
        var resolved = _registry.GetModel("test-provider", "existing-model")!;
        resolved.Name.ShouldBe("Dynamic Name");
        resolved.ContextWindow.ShouldBe(200000);
    }

    [Fact]
    public async Task DiscoverAndRegisterAsync_NullResult_PreservesExistingModels()
    {
        // Arrange: register a built-in model first
        var builtIn = CreateModel("existing-model", "Built-In Name");
        _registry.Register("test-provider", builtIn);

        var provider = new FakeDiscoveryProvider("test-provider", result: null);
        var service = new ModelDiscoveryService(_registry, [provider], _logger);

        // Act
        await service.DiscoverAndRegisterAsync();

        // Assert: built-in still present
        _registry.GetModel("test-provider", "existing-model").ShouldNotBeNull();
    }

    [Fact]
    public async Task DiscoverAndRegisterAsync_ExceptionInProvider_DoesNotThrow()
    {
        // Arrange
        var builtIn = CreateModel("existing-model", "Built-In Name");
        _registry.Register("test-provider", builtIn);

        var provider = new ThrowingDiscoveryProvider("test-provider");
        var service = new ModelDiscoveryService(_registry, [provider], _logger);

        // Act — should not throw
        await service.DiscoverAndRegisterAsync();

        // Assert: built-in still present
        _registry.GetModel("test-provider", "existing-model").ShouldNotBeNull();
    }

    [Fact]
    public async Task DiscoverAndRegisterAsync_Timeout_SkipsGracefully()
    {
        // Arrange
        var builtIn = CreateModel("existing-model", "Built-In Name");
        _registry.Register("slow-provider", builtIn);

        var provider = new SlowDiscoveryProvider("slow-provider", delay: TimeSpan.FromSeconds(30));
        var service = new ModelDiscoveryService(_registry, [provider], _logger);

        // Act — should complete quickly (10s timeout is internal)
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await service.DiscoverAndRegisterAsync(cts.Token);

        // Assert: built-in still present, no dynamic models added
        _registry.GetModel("slow-provider", "existing-model").ShouldNotBeNull();
    }

    [Fact]
    public async Task DiscoverAndRegisterAsync_MultipleProviders_IndependentlyProcessed()
    {
        // Arrange
        var provider1 = new FakeDiscoveryProvider("provider-a", [CreateModel("model-a", "Model A")]);
        var provider2 = new FakeDiscoveryProvider("provider-b", [CreateModel("model-b", "Model B")]);
        var service = new ModelDiscoveryService(_registry, [provider1, provider2], _logger);

        // Act
        await service.DiscoverAndRegisterAsync();

        // Assert
        _registry.GetModel("provider-a", "model-a").ShouldNotBeNull();
        _registry.GetModel("provider-b", "model-b").ShouldNotBeNull();
    }

    [Fact]
    public async Task DiscoverAndRegisterAsync_FailingProvider_DoesNotAffectOthers()
    {
        // Arrange
        var failingProvider = new ThrowingDiscoveryProvider("bad-provider");
        var goodProvider = new FakeDiscoveryProvider("good-provider", [CreateModel("good-model", "Good")]);
        var service = new ModelDiscoveryService(_registry, [failingProvider, goodProvider], _logger);

        // Act
        await service.DiscoverAndRegisterAsync();

        // Assert: good provider's model is registered despite the other failing
        _registry.GetModel("good-provider", "good-model").ShouldNotBeNull();
    }

    [Fact]
    public async Task DiscoverAndRegisterAsync_DoesNotRemoveBuiltInModelsNotInDiscovery()
    {
        // Arrange: register two built-in models
        _registry.Register("test-provider", CreateModel("model-a", "A"));
        _registry.Register("test-provider", CreateModel("model-b", "B"));

        // Discovery only returns model-a (not model-b)
        var provider = new FakeDiscoveryProvider("test-provider", [CreateModel("model-a", "Updated A")]);
        var service = new ModelDiscoveryService(_registry, [provider], _logger);

        // Act
        await service.DiscoverAndRegisterAsync();

        // Assert: model-b is still present (not removed)
        _registry.GetModel("test-provider", "model-a")!.Name.ShouldBe("Updated A");
        _registry.GetModel("test-provider", "model-b").ShouldNotBeNull();
    }

    private static LlmModel CreateModel(string id, string name, int contextWindow = 128000) => new(
        Id: id,
        Name: name,
        Api: "test-api",
        Provider: "test-provider",
        BaseUrl: "https://test.example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: contextWindow,
        MaxTokens: 32000);

    private sealed class FakeDiscoveryProvider : IModelDiscoveryProvider
    {
        public string ProviderKey { get; }
        private readonly IReadOnlyList<LlmModel>? _result;

        public FakeDiscoveryProvider(string providerKey, IReadOnlyList<LlmModel>? result)
        {
            ProviderKey = providerKey;
            _result = result;
        }

        public Task<IReadOnlyList<LlmModel>?> DiscoverModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_result);
    }

    private sealed class ThrowingDiscoveryProvider : IModelDiscoveryProvider
    {
        public string ProviderKey { get; }
        public ThrowingDiscoveryProvider(string providerKey) => ProviderKey = providerKey;

        public Task<IReadOnlyList<LlmModel>?> DiscoverModelsAsync(CancellationToken cancellationToken = default)
            => throw new HttpRequestException("Network error");
    }

    private sealed class SlowDiscoveryProvider : IModelDiscoveryProvider
    {
        public string ProviderKey { get; }
        private readonly TimeSpan _delay;

        public SlowDiscoveryProvider(string providerKey, TimeSpan delay)
        {
            ProviderKey = providerKey;
            _delay = delay;
        }

        public async Task<IReadOnlyList<LlmModel>?> DiscoverModelsAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return [new LlmModel("slow-model", "Slow", "test", "slow-provider", "", false, ["text"], new ModelCost(0,0,0,0), 128000, 32000)];
        }
    }
}
