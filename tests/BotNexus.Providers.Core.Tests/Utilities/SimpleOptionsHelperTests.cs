using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;
using FluentAssertions;

namespace BotNexus.Providers.Core.Tests.Utilities;

public class SimpleOptionsHelperTests
{
    private static LlmModel MakeModel(int maxTokens = 16384) => new(
        Id: "test-model",
        Name: "Test",
        Api: "test-api",
        Provider: "test",
        BaseUrl: "https://example.com",
        Reasoning: true,
        Input: ["text"],
        Cost: new ModelCost(0, 0, 0, 0),
        ContextWindow: 200000,
        MaxTokens: maxTokens);

    [Fact]
    public void BuildBaseOptions_CopiesTemperatureAndMaxTokens()
    {
        var model = MakeModel();
        var simple = new SimpleStreamOptions
        {
            Temperature = 0.7f,
            MaxTokens = 4096
        };

        var result = SimpleOptionsHelper.BuildBaseOptions(model, simple, "api-key-123");

        result.Temperature.Should().Be(0.7f);
        result.MaxTokens.Should().Be(4096);
        result.ApiKey.Should().Be("api-key-123");
    }

    [Fact]
    public void BuildBaseOptions_NullOptions_UsesDefaults()
    {
        var model = MakeModel();

        var result = SimpleOptionsHelper.BuildBaseOptions(model, null, "key");

        result.Temperature.Should().BeNull();
        result.MaxTokens.Should().Be(16384);
        result.Transport.Should().Be(Transport.Sse);
        result.CacheRetention.Should().Be(CacheRetention.Short);
    }

    [Fact]
    public void BuildBaseOptions_NullApiKey_FallsBackToOptionsApiKey()
    {
        var model = MakeModel();
        var simple = new SimpleStreamOptions
        {
            ApiKey = "fallback-key"
        };

        var result = SimpleOptionsHelper.BuildBaseOptions(model, simple, null!);

        result.ApiKey.Should().Be("fallback-key");
    }

    [Fact]
    public void BuildBaseOptions_EmptyApiKey_FallsBackToOptionsApiKey()
    {
        var model = MakeModel();
        var simple = new SimpleStreamOptions
        {
            ApiKey = "fallback-key"
        };

        var result = SimpleOptionsHelper.BuildBaseOptions(model, simple, string.Empty);

        result.ApiKey.Should().Be("fallback-key");
    }

    [Theory]
    [InlineData(16000, 16000)]
    [InlineData(64000, 32000)]
    public void BuildBaseOptions_NullOptions_SetsDefaultMaxTokensToMinModelAnd32000(int modelMaxTokens, int expectedMaxTokens)
    {
        var model = MakeModel(maxTokens: modelMaxTokens);

        var result = SimpleOptionsHelper.BuildBaseOptions(model, null, "key");

        result.MaxTokens.Should().Be(expectedMaxTokens);
    }

    [Fact]
    public void ClampReasoning_ConvertsExtraHigh_ToHigh()
    {
        var result = SimpleOptionsHelper.ClampReasoning(ThinkingLevel.ExtraHigh);

        result.Should().Be(ThinkingLevel.High);
    }

    [Fact]
    public void ClampReasoning_PassesThroughOtherLevels()
    {
        SimpleOptionsHelper.ClampReasoning(ThinkingLevel.Medium).Should().Be(ThinkingLevel.Medium);
        SimpleOptionsHelper.ClampReasoning(ThinkingLevel.Low).Should().Be(ThinkingLevel.Low);
        SimpleOptionsHelper.ClampReasoning(ThinkingLevel.High).Should().Be(ThinkingLevel.High);
        SimpleOptionsHelper.ClampReasoning(ThinkingLevel.Minimal).Should().Be(ThinkingLevel.Minimal);
    }

    [Fact]
    public void ClampReasoning_Null_ReturnsNull()
    {
        SimpleOptionsHelper.ClampReasoning(null).Should().BeNull();
    }

    [Fact]
    public void AdjustMaxTokensForThinking_CalculatesCorrectBudget()
    {
        var model = MakeModel(maxTokens: 16384);

        var (maxTokens, thinkingBudget) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
            model, requestedMaxTokens: 8000, thinkingBudget: 4000);

        maxTokens.Should().Be(12000);
        thinkingBudget.Should().Be(4000);
    }

    [Fact]
    public void AdjustMaxTokensForThinking_SmallMaxTokens_AdjustsUp()
    {
        var model = MakeModel(maxTokens: 16384);

        var (maxTokens, thinkingBudget) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
            model, requestedMaxTokens: 2000, thinkingBudget: 4000);

        maxTokens.Should().Be(6000);
        thinkingBudget.Should().Be(4000);
    }

    [Fact]
    public void AdjustMaxTokensForThinking_CapsAtModelMax()
    {
        var model = MakeModel(maxTokens: 8192);

        var (maxTokens, _) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
            model, requestedMaxTokens: 20000, thinkingBudget: 4000);

        maxTokens.Should().BeLessThanOrEqualTo(8192);
    }

    [Fact]
    public void GetBudgetForLevel_ReturnsMatchingBudget()
    {
        var budgets = new ThinkingBudgets
        {
            Medium = 8000
        };

        var result = SimpleOptionsHelper.GetBudgetForLevel(ThinkingLevel.Medium, budgets);

        result.Should().Be(8000);
    }

    [Fact]
    public void GetBudgetForLevel_NullBudgets_ReturnsNull()
    {
        var result = SimpleOptionsHelper.GetBudgetForLevel(ThinkingLevel.Medium, null);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, 1024)]
    [InlineData(ThinkingLevel.Low, 2048)]
    [InlineData(ThinkingLevel.Medium, 8192)]
    [InlineData(ThinkingLevel.High, 16384)]
    public void GetDefaultThinkingBudget_ReturnsCorrectDefaults(ThinkingLevel level, int expectedBudget)
    {
        var result = SimpleOptionsHelper.GetDefaultThinkingBudget(level);

        result.Should().Be(expectedBudget);
    }
}
