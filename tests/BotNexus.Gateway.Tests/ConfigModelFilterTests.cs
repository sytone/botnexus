using BotNexus.Gateway.Configuration;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;

namespace BotNexus.Gateway.Tests;

public sealed class ConfigModelFilterTests
{
    [Fact]
    public void GetProviders_WhenProviderConfigMissing_ReturnsAllProvidersSorted()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("anthropic", CreateModel("anthropic", "claude-sonnet-4", "Claude Sonnet 4"));
        var filter = CreateFilter(registry, new PlatformConfig());

        var providers = filter.GetProviders();

        providers.Should().Equal("anthropic", "openai");
    }

    [Fact]
    public void GetProviders_WhenConfigured_FiltersDisabledProvidersAndKeepsUnconfiguredEnabled()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("anthropic", CreateModel("anthropic", "claude-sonnet-4", "Claude Sonnet 4"));
        registry.Register("github-copilot", CreateModel("github-copilot", "gpt-4o", "Copilot GPT-4o"));
        var filter = CreateFilter(registry, new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderConfig { Enabled = false },
                ["anthropic"] = new ProviderConfig { Enabled = true }
            }
        });

        var providers = filter.GetProviders();

        providers.Should().Equal("anthropic", "github-copilot");
    }

    [Fact]
    public void GetModels_WhenProviderAllowlistNull_ReturnsAllProviderModels()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("openai", CreateModel("openai", "gpt-4.1", "GPT-4.1"));
        var filter = CreateFilter(registry, new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderConfig { Models = null }
            }
        });

        var models = filter.GetModels("openai");

        models.Select(model => model.Id).Should().BeEquivalentTo(["gpt-4o", "gpt-4.1"]);
    }

    [Fact]
    public void GetModels_WhenProviderAllowlistSet_ReturnsOnlyAllowlistedModels()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("openai", CreateModel("openai", "gpt-4.1", "GPT-4.1"));
        var filter = CreateFilter(registry, new PlatformConfig
        {
            Providers = new Dictionary<string, ProviderConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["openai"] = new ProviderConfig { Models = ["gPt-4O"] }
            }
        });

        var models = filter.GetModels("openai");

        models.Select(model => model.Id).Should().Equal("gpt-4o");
    }

    [Fact]
    public void GetModelsForAgent_WhenAgentAllowlistEmpty_ReturnsProviderModels()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("openai", CreateModel("openai", "gpt-4.1", "GPT-4.1"));
        var filter = CreateFilter(registry, new PlatformConfig());

        var models = filter.GetModelsForAgent("openai", []);

        models.Select(model => model.Id).Should().BeEquivalentTo(["gpt-4o", "gpt-4.1"]);
    }

    [Fact]
    public void GetModelsForAgent_WhenAgentAllowlistProvided_IntersectsWithProviderModels()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("openai", CreateModel("openai", "gpt-4.1", "GPT-4.1"));
        var filter = CreateFilter(registry, new PlatformConfig());

        var models = filter.GetModelsForAgent("openai", ["GPT-4.1"]);

        models.Select(model => model.Id).Should().Equal("gpt-4.1");
    }

    private static ConfigModelFilter CreateFilter(ModelRegistry registry, PlatformConfig config)
    {
        var options = new Mock<IOptionsMonitor<PlatformConfig>>();
        options.Setup(monitor => monitor.CurrentValue).Returns(config);
        return new ConfigModelFilter(registry, options.Object);
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
