using BotNexus.Providers.Core.Compatibility;
using BotNexus.Providers.Core.Models;
using FluentAssertions;

namespace BotNexus.Providers.OpenAI.Tests;

public class CompatDetectorTests
{
    [Fact]
    public void DefaultCompat_HasStandardDefaults()
    {
        var compat = new OpenAICompletionsCompat();

        compat.SupportsStore.Should().BeTrue();
        compat.SupportsDeveloperRole.Should().BeTrue();
        compat.SupportsReasoningEffort.Should().BeTrue();
        compat.SupportsUsageInStreaming.Should().BeTrue();
        compat.MaxTokensField.Should().Be("max_completion_tokens");
        compat.SupportsStrictMode.Should().BeTrue();
        compat.RequiresThinkingAsText.Should().BeFalse();
        compat.ThinkingFormat.Should().Be("openai");
    }

    [Fact]
    public void CerebrasCompat_NonStandard_NoStoreNoDeveloperRole()
    {
        var compat = new OpenAICompletionsCompat
        {
            SupportsStore = false,
            SupportsDeveloperRole = false,
            SupportsReasoningEffort = false,
            SupportsUsageInStreaming = false,
            MaxTokensField = "max_tokens",
            SupportsStrictMode = false
        };

        compat.SupportsStore.Should().BeFalse();
        compat.SupportsDeveloperRole.Should().BeFalse();
        compat.SupportsReasoningEffort.Should().BeFalse();
        compat.MaxTokensField.Should().Be("max_tokens");
    }

    [Fact]
    public void XaiCompat_NoReasoningEffort()
    {
        var compat = new OpenAICompletionsCompat
        {
            SupportsReasoningEffort = false
        };

        compat.SupportsReasoningEffort.Should().BeFalse();
        compat.SupportsDeveloperRole.Should().BeTrue(); // default
    }

    [Fact]
    public void ZaiCompat_ThinkingFormat()
    {
        var compat = new OpenAICompletionsCompat
        {
            ThinkingFormat = "zai"
        };

        compat.ThinkingFormat.Should().Be("zai");
    }

    [Fact]
    public void ModelWithExplicitCompat_OverridesDefaults()
    {
        var customCompat = new OpenAICompletionsCompat
        {
            SupportsStore = false,
            RequiresThinkingAsText = true,
            RequiresToolResultName = true,
            RequiresAssistantAfterToolResult = true
        };
        var model = TestHelpers.MakeModel(compat: customCompat);

        model.Compat.Should().NotBeNull();
        model.Compat!.SupportsStore.Should().BeFalse();
        model.Compat.RequiresThinkingAsText.Should().BeTrue();
        model.Compat.RequiresToolResultName.Should().BeTrue();
        model.Compat.RequiresAssistantAfterToolResult.Should().BeTrue();
    }

    [Fact]
    public void ReasoningEffortMap_CanBeCustomized()
    {
        var compat = new OpenAICompletionsCompat
        {
            ReasoningEffortMap = new Dictionary<ThinkingLevel, string>
            {
                [ThinkingLevel.Low] = "minimal",
                [ThinkingLevel.Medium] = "standard",
                [ThinkingLevel.High] = "maximum"
            }
        };

        compat.ReasoningEffortMap.Should().ContainKey(ThinkingLevel.Medium);
        compat.ReasoningEffortMap[ThinkingLevel.Medium].Should().Be("standard");
    }
}
