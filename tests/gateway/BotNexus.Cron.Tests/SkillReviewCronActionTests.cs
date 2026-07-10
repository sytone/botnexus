using BotNexus.Cron.Actions;
using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Tests;

public sealed class SkillReviewCronActionTests
{
    private static SkillReviewSignals TriggeringSignals() => new()
    {
        ToolCallCount = 6,
        SkillWasLoaded = true,
        UserCorrectedOrFrustrated = false,
        DiscoveredReusableWorkflow = false,
        SkillManageFailed = false,
        LoadedSkillFoundStale = false
    };

    // --- Config gating: default OFF, non-breaking ---

    [Fact]
    public void ShouldTriggerReview_DisabledConfig_ReturnsFalseEvenWhenSignalsQualify()
    {
        var config = new SkillReviewConfig { Enabled = false };
        SkillReviewCronAction.ShouldTriggerReview(TriggeringSignals(), config).ShouldBeFalse();
    }

    [Fact]
    public void SkillReviewConfig_DefaultsToDisabled()
    {
        // Non-breaking default: review must be off unless explicitly enabled.
        new SkillReviewConfig().Enabled.ShouldBeFalse();
    }

    // --- Trigger conditions (each qualifying signal fires when enabled) ---

    [Fact]
    public void ShouldTriggerReview_FiveOrMoreToolCalls_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { ToolCallCount = 5 };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_FewerThanFiveToolCallsAndNoOtherSignal_DoesNotTrigger()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { ToolCallCount = 4 };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeFalse();
    }

    [Fact]
    public void ShouldTriggerReview_SkillWasLoaded_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { ToolCallCount = 1, SkillWasLoaded = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_UserCorrectedOrFrustrated_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { UserCorrectedOrFrustrated = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_DiscoveredReusableWorkflow_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { DiscoveredReusableWorkflow = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_SkillManageFailed_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { SkillManageFailed = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_LoadedSkillFoundStale_Triggers()
    {
        var config = new SkillReviewConfig { Enabled = true };
        var signals = new SkillReviewSignals { LoadedSkillFoundStale = true };
        SkillReviewCronAction.ShouldTriggerReview(signals, config).ShouldBeTrue();
    }

    [Fact]
    public void ShouldTriggerReview_EnabledButNoQualifyingSignal_DoesNotTrigger()
    {
        var config = new SkillReviewConfig { Enabled = true };
        SkillReviewCronAction.ShouldTriggerReview(new SkillReviewSignals(), config).ShouldBeFalse();
    }

    // --- Configurable tool-call threshold ---

    [Fact]
    public void ShouldTriggerReview_RespectsCustomToolCallThreshold()
    {
        var config = new SkillReviewConfig { Enabled = true, MinToolCalls = 10 };
        SkillReviewCronAction.ShouldTriggerReview(new SkillReviewSignals { ToolCallCount = 6 }, config).ShouldBeFalse();
        SkillReviewCronAction.ShouldTriggerReview(new SkillReviewSignals { ToolCallCount = 10 }, config).ShouldBeTrue();
    }

    // --- Restricted toolset ---

    [Fact]
    public void AllowedTools_ContainsOnlySkillManageAndInspectionTools()
    {
        var allowed = SkillReviewCronAction.AllowedTools;

        allowed.ShouldContain("skill_manage");
        // Read/inspection tools are allowed.
        allowed.ShouldContain("read");
        allowed.ShouldContain("skills");
        // No arbitrary mutation / execution tools outside the skill surface.
        allowed.ShouldNotContain("exec");
        allowed.ShouldNotContain("shell");
        allowed.ShouldNotContain("write");
        allowed.ShouldNotContain("edit");
    }

    // --- Reviewer prompt: preference order ---

    [Fact]
    public void BuildReviewPrompt_EncodesPreferenceOrder()
    {
        var prompt = SkillReviewCronAction.BuildReviewPrompt(
            AgentId.From("farnsworth"), TriggeringSignals(), sessionSummary: "did a thing");

        prompt.ShouldContain("Patch a currently loaded skill");
        prompt.ShouldContain("Patch an existing umbrella skill");
        prompt.ShouldContain("supporting file");
        prompt.ShouldContain("Create a new");
        prompt.ShouldContain("skill_manage");
        prompt.ShouldContain("farnsworth");
    }

    // --- Reviewer prompt: avoid-list behaviour ---

    [Fact]
    public void BuildReviewPrompt_EncodesAvoidList()
    {
        var prompt = SkillReviewCronAction.BuildReviewPrompt(
            AgentId.From("nova"), TriggeringSignals(), sessionSummary: null);

        // one-off task narratives
        prompt.ShouldContain("one-off", Case.Insensitive);
        // PR/issue numbers as skill names
        prompt.ShouldContain("PR", Case.Insensitive);
        prompt.ShouldContain("issue number", Case.Insensitive);
        // transient environment/setup failures as durable constraints
        prompt.ShouldContain("transient", Case.Insensitive);
        // negative "tool X is broken" claims
        prompt.ShouldContain("broken", Case.Insensitive);
    }

    [Fact]
    public void BuildReviewPrompt_RestrictsToolsetInInstructions()
    {
        var prompt = SkillReviewCronAction.BuildReviewPrompt(
            AgentId.From("agent"), TriggeringSignals(), sessionSummary: null);

        prompt.ShouldContain("skill_manage");
        // Explicitly tells the reviewer it may not run arbitrary exec/write.
        prompt.ShouldContain("restricted", Case.Insensitive);
    }

    [Fact]
    public void ActionType_ReturnsExpectedValue()
    {
        new SkillReviewCronAction().ActionType.ShouldBe("skill-review");
    }
}
