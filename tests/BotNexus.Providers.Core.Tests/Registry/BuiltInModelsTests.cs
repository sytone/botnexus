using BotNexus.Providers.Core.Registry;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Registry;

public sealed class BuiltInModelsTests
{
    [Fact]
    public void RegisterAll_WhenCalled_RegistersCopilotModels()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var models = registry.GetModels("github-copilot");

        models.Should().NotBeEmpty();
    }

    [Fact]
    public void RegisterAll_WhenCalled_RegistersAnthropicDirectModels()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var models = registry.GetModels("anthropic");

        models.Should().Contain(model => model.Id == "claude-sonnet-4-20250514");
        models.Should().Contain(model => model.Id == "claude-opus-4-5-20250929");
        models.Should().Contain(model => model.Id.StartsWith("claude-sonnet-4", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisterAll_WhenCalled_RegistersOpenAIDirectModels()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var models = registry.GetModels("openai");

        models.Should().Contain(model => model.Id == "gpt-4.1");
        models.Should().Contain(model => model.Id == "gpt-4o");
    }

    [Fact]
    public void RegisterAll_WhenCalled_RegistersModelsWithRequiredFields()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var models = registry.GetProviders()
            .SelectMany(registry.GetModels)
            .ToList();

        models.Should().OnlyContain(model =>
            !string.IsNullOrWhiteSpace(model.Id) &&
            model.ContextWindow > 0 &&
            model.MaxTokens > 0);
    }

    [Fact]
    public void RegisterAll_WhenCalled_RegistersExpectedProviderCatalogs()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);

        registry.GetModels("github-copilot").Should().NotBeEmpty();
        registry.GetModels("anthropic").Should().NotBeEmpty();
        registry.GetModels("openai").Should().NotBeEmpty();
    }

    [Fact]
    public void RegisterAll_WhenCalled_OpenAiAndAnthropicModelsHaveKeyFields()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var directModels = registry.GetModels("anthropic")
            .Concat(registry.GetModels("openai"))
            .ToList();

        directModels.Should().NotBeEmpty();
        directModels.Should().OnlyContain(model =>
            !string.IsNullOrWhiteSpace(model.Id) &&
            model.ContextWindow > 0 &&
            model.MaxTokens > 0);
    }
}
