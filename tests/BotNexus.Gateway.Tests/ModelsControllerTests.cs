using BotNexus.Gateway.Api.Controllers;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

public sealed class ModelsControllerTests
{
    [Fact]
    public void GetModels_WhenNoModelsRegistered_ReturnsEmptyList()
    {
        var controller = new ModelsController(new ModelRegistry());

        var result = controller.GetModels();

        var models = (result.Result as OkObjectResult)?.Value as IEnumerable<ModelInfo>;
        models.Should().NotBeNull();
        models!.Should().BeEmpty();
    }

    [Fact]
    public void GetModels_WhenModelsRegistered_ReturnsAllModels()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("anthropic", CreateModel("anthropic", "claude-sonnet-4", "Claude Sonnet 4"));
        var controller = new ModelsController(registry);

        var result = controller.GetModels();

        var models = (result.Result as OkObjectResult)?.Value as IEnumerable<ModelInfo>;
        models.Should().NotBeNull();
        models!.Select(m => m.Name).Should().BeEquivalentTo(["GPT-4o", "Claude Sonnet 4"]);
    }

    [Fact]
    public void GetModels_ReturnsModelsSortedAlphabeticallyByName()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("anthropic", CreateModel("anthropic", "claude-sonnet-4", "Claude Sonnet 4"));
        registry.Register("anthropic", CreateModel("anthropic", "claude-haiku", "Anthropic Haiku"));
        var controller = new ModelsController(registry);

        var result = controller.GetModels();

        var models = (result.Result as OkObjectResult)?.Value as IEnumerable<ModelInfo>;
        models.Should().NotBeNull();
        models!.Select(m => m.Name).Should().Equal("Anthropic Haiku", "Claude Sonnet 4", "GPT-4o");
    }

    private static LlmModel CreateModel(string provider, string id, string name) =>
        new(
            Id: id,
            Name: name,
            Api: "test-api",
            Provider: provider,
            BaseUrl: "https://example.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 4096,
            MaxTokens: 1024);
}
