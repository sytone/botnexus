using BotNexus.Agent.Providers.Core.Compatibility;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.OpenAI.Tests;

public class CompatDetectorTests
{
    [Fact]
    public void DefaultCompat_HasStandardDefaults()
    {
        var compat = new OpenAICompletionsCompat();

        compat.SupportsStore.ShouldBeTrue();
        compat.SupportsDeveloperRole.ShouldBeTrue();
        compat.SupportsReasoningEffort.ShouldBeTrue();
        compat.SupportsUsageInStreaming.ShouldBeTrue();
        compat.MaxTokensField.ShouldBe("max_completion_tokens");
        compat.SupportsStrictMode.ShouldBeTrue();
        compat.RequiresThinkingAsText.ShouldBeFalse();
        compat.ThinkingFormat.ShouldBe("openai");
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

        compat.SupportsStore.ShouldBeFalse();
        compat.SupportsDeveloperRole.ShouldBeFalse();
        compat.SupportsReasoningEffort.ShouldBeFalse();
        compat.MaxTokensField.ShouldBe("max_tokens");
    }

    [Fact]
    public void XaiCompat_NoReasoningEffort()
    {
        var compat = new OpenAICompletionsCompat
        {
            SupportsReasoningEffort = false
        };

        compat.SupportsReasoningEffort.ShouldBeFalse();
        compat.SupportsDeveloperRole.ShouldBeTrue(); // default
    }

    [Fact]
    public void ZaiCompat_ThinkingFormat()
    {
        var compat = new OpenAICompletionsCompat
        {
            ThinkingFormat = "zai"
        };

        compat.ThinkingFormat.ShouldBe("zai");
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

        model.Compat.ShouldNotBeNull();
        model.Compat!.SupportsStore.ShouldBeFalse();
        model.Compat.RequiresThinkingAsText.ShouldBeTrue();
        model.Compat.RequiresToolResultName.ShouldBeTrue();
        model.Compat.RequiresAssistantAfterToolResult.ShouldBeTrue();
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

        compat.ReasoningEffortMap.ShouldContainKey(ThinkingLevel.Medium);
        compat.ReasoningEffortMap[ThinkingLevel.Medium].ShouldBe("standard");
    }
}
