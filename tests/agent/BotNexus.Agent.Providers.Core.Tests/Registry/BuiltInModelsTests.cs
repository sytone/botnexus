using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.Core.Tests.Registry;

public sealed class BuiltInModelsTests
{
    [Fact]
    public void RegisterAll_WhenCalled_RegistersCopilotModels()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var models = registry.GetModels("github-copilot");

        models.ShouldNotBeEmpty();
    }

    [Fact]
    public void RegisterAll_WhenCalled_RegistersAnthropicDirectModels()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var models = registry.GetModels("anthropic");

        // Ids are exact and dated, and a retired one fails with a 404 at send time rather than
        // at save time. These were replaced when the registry dropped ids the Anthropic API no
        // longer serves: claude-sonnet-4-20250514 was retired outright, and the opus 4.5 entry
        // carried the wrong release date (-20250929 for a -20251101 release).
        models.ShouldContain(model => model.Id == "claude-sonnet-5");
        models.ShouldContain(model => model.Id == "claude-opus-4-5-20251101");
        models.ShouldContain(model => model.Id.StartsWith("claude-sonnet-4", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisterAll_WhenCalled_RegistersOpenAIDirectModels()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var models = registry.GetModels("openai");

        models.ShouldContain(model => model.Id == "gpt-4.1");
        models.ShouldContain(model => model.Id == "gpt-4o");
    }

    [Fact]
    public void RegisterAll_WhenCalled_RegistersModelsWithRequiredFields()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var models = registry.GetProviders()
            .SelectMany(registry.GetModels)
            .ToList();

        models.ShouldAllBe(model =>
            !string.IsNullOrWhiteSpace(model.Id) &&
            model.ContextWindow > 0 &&
            model.MaxTokens > 0);
    }

    [Fact]
    public void RegisterAll_WhenCalled_RegistersExpectedProviderCatalogs()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);

        registry.GetModels("github-copilot").ShouldNotBeEmpty();
        registry.GetModels("anthropic").ShouldNotBeEmpty();
        registry.GetModels("openai").ShouldNotBeEmpty();
    }

    [Fact]
    public void RegisterAll_WhenCalled_OpenAiAndAnthropicModelsHaveKeyFields()
    {
        var registry = new ModelRegistry();

        new BuiltInModels().RegisterAll(registry);
        var directModels = registry.GetModels("anthropic")
            .Concat(registry.GetModels("openai"))
            .ToList();

        directModels.ShouldNotBeEmpty();
        directModels.ShouldAllBe(model =>
            !string.IsNullOrWhiteSpace(model.Id) &&
            model.ContextWindow > 0 &&
            model.MaxTokens > 0);
    }
}
