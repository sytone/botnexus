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
    /// <summary>The exact former todo instruction recovered from 6111a00c0^.</summary>
    public const string FormerTodoInstruction =
        "Advance ONE item per turn; only a tool result this turn may flip an item to [x] done -- narration cannot.";

    /// <summary>The exact former tool-result wait instruction recovered from 6111a00c0^.</summary>
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

        lines.Add("<behavior_evaluation>");
        lines.Add($"Serving provider/model: {providerId}/{modelId}. Guidance rung under evaluation: {rung}.");
        lines.Add("Complete the supplied multi-step task in this run. Use the todo tool as a checklist and perform each named operation tool exactly once in this order: initial todo, inspect_fixture, revised todo with the discovered item, apply_change, check_configuration, validate_fixture_schema, inspect_change_diff, compile_harness, compile_contract_tests, review_compile_diagnostics, verify_change, final todo. The checkpoint tools represent required validation work, not no-op calls. Verify the result and finish with a concise summary.");
        if (mutation.HasFlag(PromptMutation.FormerTodoInstruction))
            lines.Add(FormerTodoInstruction);
        if (mutation.HasFlag(PromptMutation.FormerResultWaitInstruction))
            lines.Add(FormerResultWaitInstruction);
        lines.Add("</behavior_evaluation>");
        return string.Join('\n', lines);
    }
}
