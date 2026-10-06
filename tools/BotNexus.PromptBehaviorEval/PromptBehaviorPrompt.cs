using BotNexus.Gateway.Prompts;

namespace BotNexus.PromptBehaviorEval;

/// <summary>Names the model-guidance rung under evaluation independently of the serving model.</summary>
public enum PromptGuidanceRung
{
    /// <summary>Conservative guidance shared by every model family.</summary>
    Default,
    /// <summary>Shared guidance plus the Claude overlay.</summary>
    Claude,
    /// <summary>Shared guidance plus the GPT overlay.</summary>
    Gpt,
}

/// <summary>Historical prompt instructions that can be injected independently for regression experiments.</summary>
[Flags]
public enum PromptMutation
{
    /// <summary>Use current prompt guidance unchanged.</summary>
    None = 0,
    /// <summary>Inject the former todo instruction from 6111a00c0^.</summary>
    FormerTodoInstruction = 1,
    /// <summary>Inject the former result-wait instruction from 6111a00c0^.</summary>
    FormerResultWaitInstruction = 2,
}

/// <summary>Builds a production prompt-rung slice with optional historical mutations.</summary>
public static class PromptBehaviorPrompt
{
    /// <summary>The exact former todo instruction recovered from 37b566531^.</summary>
    public const string FormerTodoInstruction =
        "Advance ONE item per turn; only a tool result this turn may flip an item to [x] done -- narration cannot.";

    /// <summary>The exact former tool-result wait instruction recovered from 37b566531^.</summary>
    public const string FormerResultWaitInstruction =
        "For multi-step work you may describe at most ONE step as completed per turn: emit the tool call for the current step, then STOP and wait for its real result before claiming progress.";

    /// <summary>Builds tool enforcement and the explicitly requested guidance rung.</summary>
    public static string Build(PromptGuidanceRung rung, PromptMutation mutation, string modelId, string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var (guidanceModel, guidanceProvider) = rung switch
        {
            PromptGuidanceRung.Claude => ("claude-sonnet-4-5", "anthropic"),
            PromptGuidanceRung.Gpt => ("gpt-5", "openai"),
            _ => ("behavior-eval-unknown", "behavior-eval-unknown"),
        };
        var context = new PromptContext
        {
            WorkspaceDir = Environment.CurrentDirectory,
            Extensions = new Dictionary<string, object?>
            {
                [ModelGuidanceSection.ModelIdExtensionKey] = guidanceModel,
                [ModelGuidanceSection.ProviderIdExtensionKey] = guidanceProvider,
            },
        };
        var lines = new PromptPipeline()
            .Add(ToolEnforcementSection.Create())
            .Add(ModelGuidanceSection.Create())
            .BuildLines(context)
            .ToList();

        ReplaceCurrentInstruction(
            lines,
            "For multi-step work, never claim a step's outcome before its tool result arrives:",
            FormerResultWaitInstruction,
            mutation.HasFlag(PromptMutation.FormerResultWaitInstruction));

        lines.Add("<behavior_evaluation>");
        lines.Add($"Serving provider/model: {providerId}/{modelId}. Guidance rung under evaluation: {rung}.");
        lines.Add("Use the todo tool as a checklist. The required operation order is: initial todo, inspect_fixture, revised todo with the discovered item, apply_change, check_configuration, validate_fixture_schema, inspect_change_diff, compile_harness, compile_contract_tests, review_compile_diagnostics, inspect_test_inventory, validate_prompt_assembly, review_provider_configuration, check_result_schema, inspect_cost_boundary, review_flakiness_boundary, validate_documentation, review_final_diff, verify_change, final todo. The checkpoint tools represent required validation work, not no-op calls. Before verify_change, preserve completed work as in_progress because only verify_change authorizes done status. Verify the result and finish with a concise summary.");
        lines.Add("</behavior_evaluation>");

        lines.Add("<conversation_todo>");
        lines.Add(TodoPromptFormatter.SectionHeading);
        lines.Add("Only a tool result this turn may flip an item to [x] done -- narration cannot.");
        lines.Add(mutation.HasFlag(PromptMutation.FormerTodoInstruction)
            ? FormerTodoInstruction
            : "There is no per-turn item budget: when an item is finished, continue to the next one in the same turn until the user's request is complete, subject to normal safety, approval and destructive-action boundaries.");
        if (!mutation.HasFlag(PromptMutation.FormerTodoInstruction))
            lines.Add("Revise this list as you learn: add, split, reprioritize or cancel items when investigation reveals work the original plan missed.");
        lines.Add("- [ ] Inspect the fixture and apply the requested change");
        lines.Add("- [ ] Validate and report the completed work");
        lines.Add("</conversation_todo>");
        return string.Join('\n', lines);
    }

    private static void ReplaceCurrentInstruction(
        List<string> lines,
        string currentPrefix,
        string formerInstruction,
        bool restoreFormerInstruction)
    {
        if (!restoreFormerInstruction)
            return;

        var index = lines.FindIndex(line => line.StartsWith(currentPrefix, StringComparison.Ordinal));
        if (index < 0)
            throw new InvalidOperationException($"Current prompt instruction was not found: {currentPrefix}");
        lines[index] = formerInstruction;
    }
}
