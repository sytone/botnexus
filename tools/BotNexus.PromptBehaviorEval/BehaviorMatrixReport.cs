namespace BotNexus.PromptBehaviorEval;

/// <summary>One guidance/mutation cell summarized without collapsing mixed outcomes.</summary>
public sealed record BehaviorMatrixCell(
    PromptGuidanceRung Rung,
    PromptMutation Mutation,
    int Samples,
    int Passed,
    int Failed,
    int MaximumSilentToolCalls,
    string Verdict);

/// <summary>Repeated-run evidence for one serving provider and model.</summary>
public sealed record BehaviorMatrixReport(
    string Provider,
    string Model,
    bool Complete,
    bool AllHistoricalInstructionsConsistentlyRed,
    IReadOnlyList<string> FailedRequirements,
    IReadOnlyList<BehaviorMatrixCell> Cells)
{
    /// <summary>Require two independent observations in each cell; never infer red from a missing run.</summary>
    public static BehaviorMatrixReport Summarize(IReadOnlyList<BehaviorEvalResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (results.Count == 0)
            throw new ArgumentException("At least one observed run is required.", nameof(results));
        var first = results[0];
        if (string.IsNullOrWhiteSpace(first.Provider) || string.IsNullOrWhiteSpace(first.Model))
            throw new ArgumentException("Every result needs a serving provider and model.", nameof(results));
        var unique = new HashSet<(PromptGuidanceRung, PromptMutation, DateTimeOffset)>();
        foreach (var result in results)
        {
            if (result.Provider != first.Provider || result.Model != first.Model)
                throw new ArgumentException("Cannot compare different serving providers or models.", nameof(results));
            if (!unique.Add((result.Rung, result.Mutation, result.StartedAt)))
                throw new ArgumentException("Duplicate cell/timestamp observation.", nameof(results));
            if (result.Acceptance is null || result.Metrics is null || result.CompletedAt < result.StartedAt)
                throw new ArgumentException("Incomplete observed result.", nameof(results));
        }

        var cells = new List<BehaviorMatrixCell>();
        foreach (var rung in new[] { PromptGuidanceRung.Default, PromptGuidanceRung.Claude, PromptGuidanceRung.Gpt, PromptGuidanceRung.Gemini })
        foreach (var mutation in new[]
        {
            PromptMutation.None,
            PromptMutation.FormerTodoInstruction,
            PromptMutation.FormerResultWaitInstruction,
            PromptMutation.FormerTodoInstruction | PromptMutation.FormerResultWaitInstruction,
        })
        {
            var runs = results.Where(result => result.Rung == rung && result.Mutation == mutation).ToArray();
            var passed = runs.Count(result => result.Acceptance.Passed);
            var failed = runs.Length - passed;
            var verdict = runs.Length switch
            {
                0 => "missing",
                < 2 => "insufficient-samples",
                _ when passed == runs.Length => mutation == PromptMutation.None ? "consistently-green" : "no-red-observed",
                _ when failed == runs.Length => "consistently-red",
                _ => "mixed",
            };
            cells.Add(new BehaviorMatrixCell(rung, mutation, runs.Length, passed, failed,
                runs.Length == 0 ? 0 : runs.Max(result => result.Metrics.MaximumSilentToolCallSpacing), verdict));
        }
        var complete = cells.All(cell => cell.Samples >= 2);
        var historicalRed = complete && cells.Where(cell => cell.Mutation != PromptMutation.None)
            .All(cell => cell.Verdict == "consistently-red");
        var failedRequirements = new List<string>();
        if (!complete) failedRequirements.Add("missing-or-insufficient-cells");
        if (complete && cells.Where(cell => cell.Mutation == PromptMutation.None)
                .Any(cell => cell.Verdict != "consistently-green"))
            failedRequirements.Add("current-prompt-not-consistently-green");
        if (complete && !historicalRed) failedRequirements.Add("historical-mutation-not-consistently-red");
        return new BehaviorMatrixReport(first.Provider, first.Model, complete, historicalRed, failedRequirements, cells);
    }
}
