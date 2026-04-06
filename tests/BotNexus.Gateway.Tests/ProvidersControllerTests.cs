using BotNexus.Gateway.Api.Controllers;
using BotNexus.Providers.Core.Models;
using BotNexus.Providers.Core.Registry;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests;

public sealed class ProvidersControllerTests
{
    [Fact]
    public void GetProviders_WhenNoProvidersRegistered_ReturnsEmptyList()
    {
        var controller = new ProvidersController(new ModelRegistry());

        var result = controller.GetProviders();

        var providers = (result.Result as OkObjectResult)?.Value as IEnumerable<ProviderInfo>;
        providers.Should().NotBeNull();
        providers!.Should().BeEmpty();
    }

    [Fact]
    public void GetProviders_WhenProvidersRegistered_ReturnsAllProviders()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("anthropic", CreateModel("anthropic", "claude-sonnet-4", "Claude Sonnet 4"));
        var controller = new ProvidersController(registry);

        var result = controller.GetProviders();

        var providers = (result.Result as OkObjectResult)?.Value as IEnumerable<ProviderInfo>;
        providers.Should().NotBeNull();
        providers!.Select(p => p.Name).Should().BeEquivalentTo(["openai", "anthropic"]);
    }

    [Fact]
    public void GetProviders_ReturnsProvidersSortedAlphabetically()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("anthropic", CreateModel("anthropic", "claude-sonnet-4", "Claude Sonnet 4"));
        registry.Register("github-copilot", CreateModel("github-copilot", "copilot-gpt-4o", "Copilot GPT-4o"));
        var controller = new ProvidersController(registry);

        var result = controller.GetProviders();

        var providers = (result.Result as OkObjectResult)?.Value as IEnumerable<ProviderInfo>;
        providers.Should().NotBeNull();
        providers!.Select(p => p.Name).Should().Equal("anthropic", "github-copilot", "openai");
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
