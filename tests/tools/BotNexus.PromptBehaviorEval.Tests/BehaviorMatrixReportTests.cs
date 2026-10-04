using BotNexus.PromptBehaviorEval;

namespace BotNexus.PromptBehaviorEval.Tests;

public sealed class BehaviorMatrixReportTests
{
    [Fact]
    public void Summarize_RejectsMissingCellsInsteadOfCallingIncompleteEvidenceGreen()
    {
        var report = BehaviorMatrixReport.Summarize([Sample(PromptGuidanceRung.Gpt, PromptMutation.None, true, 10)]);

        report.Complete.ShouldBeFalse();
        report.Cells.Count.ShouldBe(12);
        report.Cells.Single(cell => cell.Rung == PromptGuidanceRung.Gpt && cell.Mutation == PromptMutation.None).Verdict.ShouldBe("insufficient-samples");
        report.Cells.Single(cell => cell.Rung == PromptGuidanceRung.Claude && cell.Mutation == PromptMutation.FormerResultWaitInstruction).Verdict.ShouldBe("missing");
        report.FailedRequirements.ShouldContain("missing-or-insufficient-cells");
    }

    [Fact]
    public void Summarize_SeparatesRepeatedRedFromMixedAndAbsentRed()
    {
        var observations = Samples();
        // A failed historical-todo case in every replicate; the former result-wait instruction
        // has mixed outcomes on GPT and no red outcome on the other rungs.
        var report = BehaviorMatrixReport.Summarize(observations);

        report.Complete.ShouldBeTrue();
        report.AllHistoricalInstructionsConsistentlyRed.ShouldBeFalse();
        report.Cells.Single(cell => cell.Rung == PromptGuidanceRung.Gpt && cell.Mutation == PromptMutation.FormerTodoInstruction).Verdict.ShouldBe("consistently-red");
        report.Cells.Single(cell => cell.Rung == PromptGuidanceRung.Gpt && cell.Mutation == PromptMutation.FormerResultWaitInstruction).Verdict.ShouldBe("mixed");
        report.Cells.Single(cell => cell.Rung == PromptGuidanceRung.Default && cell.Mutation == PromptMutation.FormerResultWaitInstruction).Verdict.ShouldBe("no-red-observed");
        report.Cells.Single(cell => cell.Rung == PromptGuidanceRung.Gpt && cell.Mutation == PromptMutation.None).MaximumSilentToolCalls.ShouldBe(10);
        report.Cells.Single(cell => cell.Rung == PromptGuidanceRung.Default && cell.Mutation == PromptMutation.None).MaximumSilentToolCalls.ShouldBe(20);
        report.FailedRequirements.ShouldContain("historical-mutation-not-consistently-red");
    }

    [Fact]
    public void Summarize_RejectsMixedServingModelsAndDuplicateObservations()
    {
        var samples = Samples().ToList();
        samples[1] = samples[1] with { Model = "other-serving-model" };
        Should.Throw<ArgumentException>(() => BehaviorMatrixReport.Summarize(samples));
        samples[1] = samples[0];
        Should.Throw<ArgumentException>(() => BehaviorMatrixReport.Summarize(samples));
    }

    private static BehaviorEvalResult[] Samples()
    {
        var observations = new List<BehaviorEvalResult>();
        foreach (var rung in new[] { PromptGuidanceRung.Default, PromptGuidanceRung.Claude, PromptGuidanceRung.Gpt })
        foreach (var mutation in new[] { PromptMutation.None, PromptMutation.FormerTodoInstruction, PromptMutation.FormerResultWaitInstruction, PromptMutation.FormerTodoInstruction | PromptMutation.FormerResultWaitInstruction })
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var passed = mutation switch
            {
                PromptMutation.None => true,
                PromptMutation.FormerResultWaitInstruction => rung != PromptGuidanceRung.Gpt || repeat == 0,
                _ => false,
            };
            observations.Add(Sample(rung, mutation, passed, rung == PromptGuidanceRung.Gpt ? 10 : 20, repeat));
        }
        return observations.ToArray();
    }

    private static BehaviorEvalResult Sample(PromptGuidanceRung rung, PromptMutation mutation, bool passed, int spacing, int repeat = 0) => new(
        "same-provider", "same-serving-model", rung, mutation,
        DateTimeOffset.UnixEpoch.AddMinutes(repeat), DateTimeOffset.UnixEpoch.AddMinutes(repeat).AddSeconds(1),
        new PromptBehaviorMetrics(["todo"], [], 1, spacing, 0), [], [], false, 0,
        new BehaviorAcceptance(passed, passed ? [] : ["requiredOperationOrder"], ["todo"], []),
        [], 123, 45, "summary", "prompt");
}
