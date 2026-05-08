using BotNexus.Agent.Providers.Anthropic;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Anthropic.Tests;

public class ThinkingConfigTests
{
    [Fact]
    public void AnthropicOptions_AdaptiveThinking_ForOpus4()
    {
        var opts = new AnthropicOptions
        {
            ThinkingEnabled = true,
            Effort = "high"
        };

        opts.ThinkingEnabled.ShouldBeTrue();
        opts.Effort.ShouldBe("high");
        opts.ThinkingBudgetTokens.ShouldBeNull();
    }

    [Fact]
    public void AnthropicOptions_BudgetBasedThinking_ForOlderModels()
    {
        var opts = new AnthropicOptions
        {
            ThinkingEnabled = true,
            ThinkingBudgetTokens = 10000
        };

        opts.ThinkingEnabled.ShouldBeTrue();
        opts.ThinkingBudgetTokens.ShouldBe(10000);
        opts.Effort.ShouldBeNull();
    }

    [Fact]
    public void AnthropicOptions_ThinkingDisabled()
    {
        var opts = new AnthropicOptions
        {
            ThinkingEnabled = false
        };

        opts.ThinkingEnabled.ShouldBeFalse();
    }

    [Fact]
    public void AnthropicOptions_InterleavedThinking_DefaultTrue()
    {
        var opts = new AnthropicOptions();

        opts.InterleavedThinking.ShouldBeTrue();
    }

    [Fact]
    public void AnthropicOptions_ToolChoice_CanBeSet()
    {
        var opts = new AnthropicOptions { ToolChoice = "auto" };

        opts.ToolChoice.ShouldBe("auto");
    }

    [Fact]
    public void ThinkingBudgets_CanDefineAllLevels()
    {
        var budgets = new ThinkingBudgets
        {
            Minimal = 1024,
            Low = 4096,
            Medium = 10000,
            High = 32000,
            ExtraHigh = 64000
        };

        budgets.Minimal.ShouldBe(1024);
        budgets.High.ShouldBe(32000);
        budgets.ExtraHigh.ShouldBe(64000);
    }
}
