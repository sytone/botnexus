using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Tests.Models;

public class LlmModelTests
{
    [Fact]
    public void LlmModel_Creation_SetsAllProperties()
    {
        var cost = new ModelCost(3.0m, 15.0m, 0.3m, 3.75m);
        var model = new LlmModel(
            Id: "claude-opus-4",
            Name: "Claude Opus 4",
            Api: "anthropic-messages",
            Provider: "anthropic",
            BaseUrl: "https://api.anthropic.com",
            Reasoning: true,
            Input: ["text", "image"],
            Cost: cost,
            ContextWindow: 200000,
            MaxTokens: 16384);

        model.Id.ShouldBe("claude-opus-4");
        model.Name.ShouldBe("Claude Opus 4");
        model.Api.ShouldBe("anthropic-messages");
        model.Provider.ShouldBe("anthropic");
        model.BaseUrl.ShouldBe("https://api.anthropic.com");
        model.Reasoning.ShouldBeTrue();
        model.Input.ShouldContain("image");
        model.Cost.Input.ShouldBe(3.0m);
        model.ContextWindow.ShouldBe(200000);
        model.MaxTokens.ShouldBe(16384);
        model.SupportsExtraHighThinking.ShouldBeFalse();
    }

    [Fact]
    public void LlmModel_DefaultOptionalProperties_AreNull()
    {
        var model = new LlmModel(
            Id: "test",
            Name: "Test",
            Api: "test-api",
            Provider: "test-provider",
            BaseUrl: "https://example.com",
            Reasoning: false,
            Input: ["text"],
            Cost: new ModelCost(0, 0, 0, 0),
            ContextWindow: 4096,
            MaxTokens: 1024);

        model.Headers.ShouldBeNull();
        model.Compat.ShouldBeNull();
        model.SupportsExtraHighThinking.ShouldBeFalse();
    }

    [Fact]
    public void ModelCost_Record_SetsAllFields()
    {
        var cost = new ModelCost(1.0m, 2.0m, 0.5m, 0.75m);

        cost.Input.ShouldBe(1.0m);
        cost.Output.ShouldBe(2.0m);
        cost.CacheRead.ShouldBe(0.5m);
        cost.CacheWrite.ShouldBe(0.75m);
    }
}
