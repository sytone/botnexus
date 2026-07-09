using BotNexus.Gateway.Configuration;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;
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

        providers.ShouldBe(new[] { "anthropic", "openai" });
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

        providers.ShouldBe(new[] { "anthropic", "github-copilot" });
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

        models.Select(model => model.Id).OrderBy(id => id).ShouldBe(new[] { "gpt-4.1", "gpt-4o" });
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

        models.ShouldHaveSingleItem().Id.ShouldBe("gpt-4o");
    }

    [Fact]
    public void GetModelsForAgent_WhenAgentAllowlistEmpty_ReturnsProviderModels()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("openai", CreateModel("openai", "gpt-4.1", "GPT-4.1"));
        var filter = CreateFilter(registry, new PlatformConfig());

        var models = filter.GetModelsForAgent("openai", []);

        models.Select(model => model.Id).OrderBy(id => id).ShouldBe(new[] { "gpt-4.1", "gpt-4o" });
    }

    [Fact]
    public void GetModelsForAgent_WhenAgentAllowlistProvided_IntersectsWithProviderModels()
    {
        var registry = new ModelRegistry();
        registry.Register("openai", CreateModel("openai", "gpt-4o", "GPT-4o"));
        registry.Register("openai", CreateModel("openai", "gpt-4.1", "GPT-4.1"));
        var filter = CreateFilter(registry, new PlatformConfig());

        var models = filter.GetModelsForAgent("openai", ["GPT-4.1"]);

        models.ShouldHaveSingleItem().Id.ShouldBe("gpt-4.1");
    }

    [Fact]
    public void GetModels_DynamicReasoningModel_SurfacesFullThinkingSetToPicker()
    {
        // PBI6 (#1707): a dynamic model registered with the inferred reasoning + extra-high
        // capabilities must surface the full thinking-level set (including the top tiers) so the
        // agent + conversation pickers offer only - and all - the valid options.
        var caps = DynamicModelCapabilities.Infer("gpt-5.2");
        var registry = new ModelRegistry();
        registry.Register("custom", new LlmModel(
            Id: "gpt-5.2",
            Name: "gpt-5.2",
            Api: "openai-completions",
            Provider: "custom",
            BaseUrl: "https://example.com",
            Reasoning: caps.Reasoning,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 200000,
            MaxTokens: 32000,
            SupportsExtraHighThinking: caps.SupportsExtraHighThinking,
            SupportsExtendedContextWindow: caps.SupportsExtendedContextWindow));
        var filter = CreateFilter(registry, new PlatformConfig());

        var model = filter.GetModels("custom").ShouldHaveSingleItem();

        var thinking = model.SupportedThinkingLevels.ShouldNotBeNull();
        thinking.ShouldContain("max");
        thinking.ShouldContain("xhigh");
        thinking.Count.ShouldBe(6);
    }

    [Fact]
    public void GetModels_DynamicNonReasoningModel_OffersNoInvalidThinkingChoice()
    {
        // A dynamic non-reasoning model must offer NO thinking levels so the picker never presents
        // an invalid choice.
        var caps = DynamicModelCapabilities.Infer("llama3.1");
        var registry = new ModelRegistry();
        registry.Register("custom", new LlmModel(
            Id: "llama3.1",
            Name: "llama3.1",
            Api: "openai-completions",
            Provider: "custom",
            BaseUrl: "https://example.com",
            Reasoning: caps.Reasoning,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 128000,
            MaxTokens: 32000,
            SupportsExtraHighThinking: caps.SupportsExtraHighThinking,
            SupportsExtendedContextWindow: caps.SupportsExtendedContextWindow));
        var filter = CreateFilter(registry, new PlatformConfig());

        var model = filter.GetModels("custom").ShouldHaveSingleItem();

        model.SupportedThinkingLevels.ShouldNotBeNull().ShouldBeEmpty();
        // A standard-context model exposes exactly its single window - no invalid 1M choice.
        model.SupportedContextSizes.ShouldNotBeNull().ShouldBe(new[] { 128000 });
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
