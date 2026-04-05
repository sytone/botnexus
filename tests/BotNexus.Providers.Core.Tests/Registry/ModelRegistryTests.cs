using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Registry;

public class ModelRegistryTests : IDisposable
{
    private readonly ModelRegistry _registry = new();

    public ModelRegistryTests()
    {
        _registry.Clear();
    }

    public void Dispose()
    {
        _registry.Clear();
    }

    private static LlmModel MakeModel(string id = "test-model", string provider = "test",
        string api = "test-api", decimal inputCost = 1.0m, decimal outputCost = 2.0m) => new(
        Id: id,
        Name: id,
        Api: api,
        Provider: provider,
        BaseUrl: "https://example.com",
        Reasoning: false,
        Input: ["text"],
        Cost: new ModelCost(inputCost, outputCost, 0, 0),
        ContextWindow: 4096,
        MaxTokens: 1024);

    [Fact]
    public void Register_AndRetrieve_ReturnsModel()
    {
        var model = MakeModel();
        _registry.Register("test", model);

        var result = _registry.GetModel("test", "test-model");

        result.Should().NotBeNull();
        result!.Id.Should().Be("test-model");
    }

    [Fact]
    public void GetModel_UnknownProvider_ReturnsNull()
    {
        var result = _registry.GetModel("nonexistent", "test-model");

        result.Should().BeNull();
    }

    [Fact]
    public void GetModel_UnknownModelId_ReturnsNull()
    {
        var model = MakeModel();
        _registry.Register("test", model);

        var result = _registry.GetModel("test", "nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public void GetProviders_ReturnsRegisteredNames()
    {
        _registry.Register("provider-a", MakeModel("m1"));
        _registry.Register("provider-b", MakeModel("m2"));

        var providers = _registry.GetProviders();

        providers.Should().Contain("provider-a");
        providers.Should().Contain("provider-b");
    }

    [Fact]
    public void GetModels_ReturnsAllModelsForProvider()
    {
        _registry.Register("prov", MakeModel("m1"));
        _registry.Register("prov", MakeModel("m2"));

        var models = _registry.GetModels("prov");

        models.Should().HaveCount(2);
    }

    [Fact]
    public void GetModels_UnknownProvider_ReturnsEmpty()
    {
        var models = _registry.GetModels("unknown");

        models.Should().BeEmpty();
    }

    [Fact]
    public void CalculateCost_ComputesCorrectly()
    {
        var model = MakeModel(inputCost: 3.0m, outputCost: 15.0m);
        var usage = new Usage
        {
            Input = 1000,
            Output = 500,
            CacheRead = 0,
            CacheWrite = 0,
            TotalTokens = 1500
        };

        var cost = ModelRegistry.CalculateCost(model, usage);

        cost.Input.Should().Be(1000 * 3.0m / 1_000_000m);
        cost.Output.Should().Be(500 * 15.0m / 1_000_000m);
        cost.Total.Should().Be(cost.Input + cost.Output);
    }

    [Fact]
    public void SupportsExtraHigh_WithOpusReasoningModel_ReturnsTrue()
    {
        var model = MakeModel(id: "claude-opus-4.6") with { Reasoning = true };

        var result = ModelRegistry.SupportsExtraHigh(model);

        result.Should().BeTrue();
    }

    [Fact]
    public void SupportsExtraHigh_WithNonOpusModel_ReturnsFalse()
    {
        var model = MakeModel(id: "claude-sonnet-4.5") with { Reasoning = true };

        var result = ModelRegistry.SupportsExtraHigh(model);

        result.Should().BeFalse();
    }

    [Fact]
    public void SupportsExtraHigh_WithReasoningDisabled_ReturnsTrue()
    {
        var model = MakeModel(id: "claude-opus-4.6") with { Reasoning = false };

        var result = ModelRegistry.SupportsExtraHigh(model);

        result.Should().BeTrue();
    }

    [Fact]
    public void ModelsAreEqual_SameIdentity_ReturnsTrue()
    {
        var first = MakeModel();
        var second = first with { Cost = new ModelCost(10, 20, 0, 0) };

        var result = ModelRegistry.ModelsAreEqual(first, second);

        result.Should().BeTrue();
    }

    [Fact]
    public void ModelsAreEqual_DifferentId_ReturnsFalse()
    {
        var first = MakeModel(id: "model-a");
        var second = MakeModel(id: "model-b");

        var result = ModelRegistry.ModelsAreEqual(first, second);

        result.Should().BeFalse();
    }

    [Fact]
    public void ModelsAreEqual_DifferentProvider_ReturnsFalse()
    {
        var first = MakeModel(provider: "provider-a");
        var second = MakeModel(provider: "provider-b");

        var result = ModelRegistry.ModelsAreEqual(first, second);

        result.Should().BeFalse();
    }

    [Fact]
    public void ModelsAreEqual_DifferentBaseUrl_ReturnsTrue()
    {
        var first = MakeModel() with { BaseUrl = "https://a.example.com" };
        var second = MakeModel() with { BaseUrl = "https://b.example.com" };

        var result = ModelRegistry.ModelsAreEqual(first, second);

        result.Should().BeTrue();
    }
}
