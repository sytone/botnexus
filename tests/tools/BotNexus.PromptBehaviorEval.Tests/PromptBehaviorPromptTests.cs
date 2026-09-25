using BotNexus.PromptBehaviorEval;

namespace BotNexus.PromptBehaviorEval.Tests;

public sealed class PromptBehaviorPromptTests
{
    [Theory]
    [InlineData(PromptGuidanceRung.Default, "unknown-model", "custom-provider")]
    [InlineData(PromptGuidanceRung.Claude, "claude-sonnet-4-5", "anthropic")]
    [InlineData(PromptGuidanceRung.Gpt, "gpt-5", "openai")]
    public void Build_RequestedRung_IncludesSharedGuidance(PromptGuidanceRung rung, string modelId, string providerId)
    {
        var prompt = PromptBehaviorPrompt.Build(rung, PromptMutation.None, modelId, providerId);
        prompt.ShouldContain("Never answer from memory when a tool can verify the answer");
        prompt.ShouldContain("Never simulate or fabricate tool output");
        prompt.ShouldNotContain(PromptBehaviorPrompt.FormerTodoInstruction);
        prompt.ShouldNotContain(PromptBehaviorPrompt.FormerResultWaitInstruction);
    }

    [Fact]
    public void Build_GptRung_AloneIncludesNarrationThreshold()
    {
        var defaultPrompt = PromptBehaviorPrompt.Build(PromptGuidanceRung.Default, PromptMutation.None, "unknown-model", "custom-provider");
        var claudePrompt = PromptBehaviorPrompt.Build(PromptGuidanceRung.Claude, PromptMutation.None, "claude-sonnet-4-5", "anthropic");
        var gptPrompt = PromptBehaviorPrompt.Build(PromptGuidanceRung.Gpt, PromptMutation.None, "gpt-5", "openai");
        defaultPrompt.ShouldNotContain("at least once every ten tool calls");
        claudePrompt.ShouldNotContain("at least once every ten tool calls");
        gptPrompt.ShouldContain("at least once every ten tool calls");
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
        task.ShouldContain("check configuration");
        task.ShouldContain("compile the harness and contract-test projects");
        task.ShouldContain("review compile diagnostics");
    }

    [Theory]
    [InlineData(PromptMutation.FormerTodoInstruction, true, false)]
    [InlineData(PromptMutation.FormerResultWaitInstruction, false, true)]
    [InlineData(PromptMutation.FormerTodoInstruction | PromptMutation.FormerResultWaitInstruction, true, true)]
    public void Build_MutationsInjectExactFormerInstructionsIndependently(PromptMutation mutation, bool hasTodoInstruction, bool hasResultWaitInstruction)
    {
        var prompt = PromptBehaviorPrompt.Build(PromptGuidanceRung.Default, mutation, "unknown-model", "custom-provider");
        prompt.Contains(PromptBehaviorPrompt.FormerTodoInstruction, StringComparison.Ordinal).ShouldBe(hasTodoInstruction);
        prompt.Contains(PromptBehaviorPrompt.FormerResultWaitInstruction, StringComparison.Ordinal).ShouldBe(hasResultWaitInstruction);
    }
}
