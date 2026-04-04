using BotNexus.Providers.Anthropic;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.Anthropic.Tests;

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

        opts.ThinkingEnabled.Should().BeTrue();
        opts.Effort.Should().Be("high");
        opts.ThinkingBudgetTokens.Should().BeNull();
    }

    [Fact]
    public void AnthropicOptions_BudgetBasedThinking_ForOlderModels()
    {
        var opts = new AnthropicOptions
        {
            ThinkingEnabled = true,
            ThinkingBudgetTokens = 10000
        };

        opts.ThinkingEnabled.Should().BeTrue();
        opts.ThinkingBudgetTokens.Should().Be(10000);
        opts.Effort.Should().BeNull();
    }

    [Fact]
    public void AnthropicOptions_ThinkingDisabled()
    {
        var opts = new AnthropicOptions
        {
            ThinkingEnabled = false
        };

        opts.ThinkingEnabled.Should().BeFalse();
    }

    [Fact]
    public void AnthropicOptions_InterleavedThinking_DefaultTrue()
    {
        var opts = new AnthropicOptions();

        opts.InterleavedThinking.Should().BeTrue();
    }

    [Fact]
    public void AnthropicOptions_ToolChoice_CanBeSet()
    {
        var opts = new AnthropicOptions { ToolChoice = "auto" };

        opts.ToolChoice.Should().Be("auto");
    }

    [Fact]
    public void ThinkingBudgets_CanDefineAllLevels()
    {
        var budgets = new ThinkingBudgets
        {
            Minimal = new ThinkingBudgetLevel(1024, 4096),
            Low = new ThinkingBudgetLevel(4096, 8192),
            Medium = new ThinkingBudgetLevel(10000, 16384),
            High = new ThinkingBudgetLevel(32000, 32768),
            ExtraHigh = new ThinkingBudgetLevel(64000, 65536)
        };

        budgets.Minimal!.ThinkingBudget.Should().Be(1024);
        budgets.High!.ThinkingBudget.Should().Be(32000);
        budgets.ExtraHigh!.MaxTokens.Should().Be(65536);
    }
}
