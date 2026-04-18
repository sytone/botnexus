using BotNexus.Agent.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Models;

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

        model.Id.Should().Be("claude-opus-4");
        model.Name.Should().Be("Claude Opus 4");
        model.Api.Should().Be("anthropic-messages");
        model.Provider.Should().Be("anthropic");
        model.BaseUrl.Should().Be("https://api.anthropic.com");
        model.Reasoning.Should().BeTrue();
        model.Input.Should().Contain("image");
        model.Cost.Input.Should().Be(3.0m);
        model.ContextWindow.Should().Be(200000);
        model.MaxTokens.Should().Be(16384);
        model.SupportsExtraHighThinking.Should().BeFalse();
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

        model.Headers.Should().BeNull();
        model.Compat.Should().BeNull();
        model.SupportsExtraHighThinking.Should().BeFalse();
    }

    [Fact]
    public void ModelCost_Record_SetsAllFields()
    {
        var cost = new ModelCost(1.0m, 2.0m, 0.5m, 0.75m);

        cost.Input.Should().Be(1.0m);
        cost.Output.Should().Be(2.0m);
        cost.CacheRead.Should().Be(0.5m);
        cost.CacheWrite.Should().Be(0.75m);
    }
}
