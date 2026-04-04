using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Registry;

public class ModelRegistryTests : IDisposable
{
    public ModelRegistryTests()
    {
        ModelRegistry.Clear();
    }

    public void Dispose()
    {
        ModelRegistry.Clear();
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
        ModelRegistry.Register("test", model);

        var result = ModelRegistry.GetModel("test", "test-model");

        result.Should().NotBeNull();
        result!.Id.Should().Be("test-model");
    }

    [Fact]
    public void GetModel_UnknownProvider_ReturnsNull()
    {
        var result = ModelRegistry.GetModel("nonexistent", "test-model");

        result.Should().BeNull();
    }

    [Fact]
    public void GetModel_UnknownModelId_ReturnsNull()
    {
        var model = MakeModel();
        ModelRegistry.Register("test", model);

        var result = ModelRegistry.GetModel("test", "nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public void GetProviders_ReturnsRegisteredNames()
    {
        ModelRegistry.Register("provider-a", MakeModel("m1"));
        ModelRegistry.Register("provider-b", MakeModel("m2"));

        var providers = ModelRegistry.GetProviders();

        providers.Should().Contain("provider-a");
        providers.Should().Contain("provider-b");
    }

    [Fact]
    public void GetModels_ReturnsAllModelsForProvider()
    {
        ModelRegistry.Register("prov", MakeModel("m1"));
        ModelRegistry.Register("prov", MakeModel("m2"));

        var models = ModelRegistry.GetModels("prov");

        models.Should().HaveCount(2);
    }

    [Fact]
    public void GetModels_UnknownProvider_ReturnsEmpty()
    {
        var models = ModelRegistry.GetModels("unknown");

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
}
