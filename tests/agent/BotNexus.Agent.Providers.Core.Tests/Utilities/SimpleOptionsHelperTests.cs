using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Utilities;

namespace BotNexus.Agent.Providers.Core.Tests.Utilities;

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

        result.Temperature.ShouldBe(0.7f);
        result.MaxTokens.ShouldBe(4096);
        result.ApiKey.ShouldBe("api-key-123");
    }

    [Fact]
    public void BuildBaseOptions_NullOptions_UsesDefaults()
    {
        var model = MakeModel();

        var result = SimpleOptionsHelper.BuildBaseOptions(model, null, "key");

        result.Temperature.ShouldBeNull();
        result.MaxTokens.ShouldBe(16384);
        result.Transport.ShouldBe(Transport.Sse);
        result.CacheRetention.ShouldBe(CacheRetention.Short);
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

        result.ApiKey.ShouldBe("fallback-key");
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

        result.ApiKey.ShouldBe("fallback-key");
    }

    [Theory]
    [InlineData(16000, 16000)]
    [InlineData(64000, 32000)]
    public void BuildBaseOptions_NullOptions_SetsDefaultMaxTokensToMinModelAnd32000(int modelMaxTokens, int expectedMaxTokens)
    {
        var model = MakeModel(maxTokens: modelMaxTokens);

        var result = SimpleOptionsHelper.BuildBaseOptions(model, null, "key");

        result.MaxTokens.ShouldBe(expectedMaxTokens);
    }

    [Fact]
    public void ClampReasoning_ConvertsExtraHigh_ToHigh()
    {
        var result = SimpleOptionsHelper.ClampReasoning(ThinkingLevel.ExtraHigh);

        result.ShouldBe(ThinkingLevel.High);
    }

    [Fact]
    public void ClampReasoning_PassesThroughOtherLevels()
    {
        SimpleOptionsHelper.ClampReasoning(ThinkingLevel.Medium).ShouldBe(ThinkingLevel.Medium);
        SimpleOptionsHelper.ClampReasoning(ThinkingLevel.Low).ShouldBe(ThinkingLevel.Low);
        SimpleOptionsHelper.ClampReasoning(ThinkingLevel.High).ShouldBe(ThinkingLevel.High);
        SimpleOptionsHelper.ClampReasoning(ThinkingLevel.Minimal).ShouldBe(ThinkingLevel.Minimal);
    }

    [Fact]
    public void ClampReasoning_Null_ReturnsNull()
    {
        SimpleOptionsHelper.ClampReasoning(null).ShouldBeNull();
    }

    [Fact]
    public void AdjustMaxTokensForThinking_CalculatesCorrectBudget()
    {
        var model = MakeModel(maxTokens: 16384);

        var (maxTokens, thinkingBudget) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
            model, requestedMaxTokens: 8000, thinkingBudget: 4000);

        maxTokens.ShouldBe(12000);
        thinkingBudget.ShouldBe(4000);
    }

    [Fact]
    public void AdjustMaxTokensForThinking_SmallMaxTokens_AdjustsUp()
    {
        var model = MakeModel(maxTokens: 16384);

        var (maxTokens, thinkingBudget) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
            model, requestedMaxTokens: 2000, thinkingBudget: 4000);

        maxTokens.ShouldBe(6000);
        thinkingBudget.ShouldBe(4000);
    }

    [Fact]
    public void AdjustMaxTokensForThinking_CapsAtModelMax()
    {
        var model = MakeModel(maxTokens: 8192);

        var (maxTokens, _) = SimpleOptionsHelper.AdjustMaxTokensForThinking(
            model, requestedMaxTokens: 20000, thinkingBudget: 4000);

        maxTokens.ShouldBeLessThanOrEqualTo(8192);
    }

    [Fact]
    public void GetBudgetForLevel_ReturnsMatchingBudget()
    {
        var budgets = new ThinkingBudgets
        {
            Medium = 8000
        };

        var result = SimpleOptionsHelper.GetBudgetForLevel(ThinkingLevel.Medium, budgets);

        result.ShouldBe(8000);
    }

    [Fact]
    public void GetBudgetForLevel_NullBudgets_ReturnsNull()
    {
        var result = SimpleOptionsHelper.GetBudgetForLevel(ThinkingLevel.Medium, null);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData(ThinkingLevel.Minimal, 1024)]
    [InlineData(ThinkingLevel.Low, 2048)]
    [InlineData(ThinkingLevel.Medium, 8192)]
    [InlineData(ThinkingLevel.High, 16384)]
    [InlineData(ThinkingLevel.ExtraHigh, 16384)]
    [InlineData(ThinkingLevel.Max, 32768)]
    public void GetDefaultThinkingBudget_ReturnsCorrectDefaults(ThinkingLevel level, int expectedBudget)
    {
        var result = SimpleOptionsHelper.GetDefaultThinkingBudget(level);

        result.ShouldBe(expectedBudget);
    }

    [Fact]
    public void ClampReasoning_ConvertsMax_ToHigh()
    {
        var result = SimpleOptionsHelper.ClampReasoning(ThinkingLevel.Max);

        result.ShouldBe(ThinkingLevel.High);
    }

    [Fact]
    public void GetBudgetForLevel_Max_ReturnsMatchingBudget()
    {
        var budgets = new ThinkingBudgets
        {
            Max = 50000
        };

        var result = SimpleOptionsHelper.GetBudgetForLevel(ThinkingLevel.Max, budgets);

        result.ShouldBe(50000);
    }
}
