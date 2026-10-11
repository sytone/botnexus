using BotNexus.PromptBehaviorEval;

namespace BotNexus.PromptBehaviorEval.Tests;

public sealed class PromptBehaviorPromptTests
{
    [Theory]
    [InlineData(PromptGuidanceRung.Default, "unknown-model", "custom-provider")]
    [InlineData(PromptGuidanceRung.Claude, "claude-sonnet-4-5", "anthropic")]
    [InlineData(PromptGuidanceRung.Gpt, "gpt-5", "openai")]
    [InlineData(PromptGuidanceRung.Gemini, "gemini-2.5-pro", "google")]
    public void Build_RequestedRung_IncludesSharedGuidance(PromptGuidanceRung rung, string modelId, string providerId)
    {
        var prompt = PromptBehaviorPrompt.Build(rung, PromptMutation.None, modelId, providerId);
        prompt.ShouldContain("Never answer from memory when a tool can verify the answer");
        prompt.ShouldContain("Never simulate or fabricate tool output");
        prompt.ShouldNotContain(PromptBehaviorPrompt.FormerTodoInstruction);
        prompt.ShouldNotContain(PromptBehaviorPrompt.FormerResultWaitInstruction);
    }

    [Theory]
    [InlineData(PromptGuidanceRung.Default, "unknown-model", "custom-provider")]
    [InlineData(PromptGuidanceRung.Claude, "claude-sonnet-4-5", "anthropic")]
    [InlineData(PromptGuidanceRung.Gpt, "gpt-5", "openai")]
    [InlineData(PromptGuidanceRung.Gemini, "gemini-2.5-pro", "google")]
    public void Build_EveryRung_InheritsOneNarrationThreshold(PromptGuidanceRung rung, string modelId, string providerId)
    {
        var prompt = PromptBehaviorPrompt.Build(rung, PromptMutation.None, modelId, providerId);
        prompt.Split("at least once every ten tool calls", StringSplitOptions.None).Length.ShouldBe(2);
        if (rung == PromptGuidanceRung.Gemini)
            prompt.ShouldContain("Always use absolute paths in file operations");
    }

    [Fact]
    public void DefaultTask_RequestsTheIssueScenario()
    {
        var task = new BehaviorEvalConfiguration
        {
            Endpoint = "https://example.test/v1",
            Provider = "provider",
            Model = "model",
            ApiKeyEnvironmentVariable = "KEY",
            OutputPath = "result.json",
        }.Task;

        task.ShouldContain("start a checklist with at least two items");
        task.ShouldContain("inspect the fixture");
        task.ShouldContain("add any newly required work");
        task.ShouldContain("complete at least two checklist items");
        task.ShouldContain("complete every named validation and review operation");
        task.ShouldContain("verify completion");
    }

    [Theory]
    [InlineData(PromptMutation.FormerTodoInstruction, true, false)]
    [InlineData(PromptMutation.FormerResultWaitInstruction, false, true)]
    [InlineData(PromptMutation.FormerTodoInstruction | PromptMutation.FormerResultWaitInstruction, true, true)]
    public void Build_MutationsRestoreExactFormerInstructionsIndependently(PromptMutation mutation, bool hasTodoInstruction, bool hasResultWaitInstruction)
    {
        var prompt = PromptBehaviorPrompt.Build(PromptGuidanceRung.Default, mutation, "unknown-model", "custom-provider");
        prompt.Contains(PromptBehaviorPrompt.FormerTodoInstruction, StringComparison.Ordinal).ShouldBe(hasTodoInstruction);
        prompt.Contains(PromptBehaviorPrompt.FormerResultWaitInstruction, StringComparison.Ordinal).ShouldBe(hasResultWaitInstruction);
        prompt.Contains("There is no per-turn item budget", StringComparison.Ordinal).ShouldBe(!hasTodoInstruction);
        prompt.Contains("Revise this list as you learn", StringComparison.Ordinal).ShouldBe(!hasTodoInstruction);
        prompt.Contains("For multi-step work, never claim a step's outcome before its tool result arrives:", StringComparison.Ordinal).ShouldBe(!hasResultWaitInstruction);
    }
}
