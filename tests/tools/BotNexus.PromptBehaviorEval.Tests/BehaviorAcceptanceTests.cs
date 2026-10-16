using BotNexus.PromptBehaviorEval;

namespace BotNexus.PromptBehaviorEval.Tests;

public sealed class BehaviorAcceptanceTests
{
    private static readonly string[] ExpectedOrder =
    [
        "todo",
        "inspect_fixture",
        "todo",
        "apply_change",
        "check_configuration",
        "validate_fixture_schema",
        "inspect_change_diff",
        "compile_harness",
        "compile_contract_tests",
        "review_compile_diagnostics",
        "inspect_test_inventory",
        "validate_prompt_assembly",
        "review_provider_configuration",
        "check_result_schema",
        "inspect_cost_boundary",
        "review_flakiness_boundary",
        "validate_documentation",
        "review_final_diff",
        "verify_change",
        "todo",
    ];

    [Fact]
    public void Evaluate_CurrentGptBehavior_PassesEveryRequiredCheck()
    {
        var acceptance = BehaviorAcceptance.Evaluate(PromptGuidanceRung.Gpt, ExpectedOrder, ExpectedOrder, true, 2, 10, 0, 0);

        acceptance.Passed.ShouldBeTrue();
        acceptance.FailedChecks.ShouldBeEmpty();
        acceptance.ExpectedOperationOrder.ShouldBe(ExpectedOrder);
        acceptance.Checks.ShouldContain(check => check.Name == "narrationSpacing" && check.Passed);
    }

    [Fact]
    public void Evaluate_MissingRequiredBehavior_ReportsEveryFailedReason()
    {
        string[] insufficientActualOrder = ["todo", "inspect_fixture", "apply_change"];
        string[] insufficientExpectedOrder = ["todo", "inspect_fixture", "apply_change", "verify_change"];
        var acceptance = BehaviorAcceptance.Evaluate(PromptGuidanceRung.Gpt, insufficientActualOrder, insufficientExpectedOrder, false, 1, 11, 1, 1);

        acceptance.Passed.ShouldBeFalse();
        acceptance.FailedChecks.ShouldContain("requiredOperationOrder");
        acceptance.FailedChecks.ShouldContain("discoveredItemAddedAfterInspection");
        acceptance.FailedChecks.ShouldContain("atLeastTwoDistinctDoneItems");
        acceptance.FailedChecks.ShouldContain("noRejectedTodoTransitions");
        acceptance.FailedChecks.ShouldContain("narrationSpacing");
        acceptance.FailedChecks.ShouldContain("operationSequenceExceedsNarrationThreshold");
        acceptance.FailedChecks.ShouldContain("noRejectedOperations");
    }

    [Theory]
    [InlineData(PromptGuidanceRung.Default)]
    [InlineData(PromptGuidanceRung.Claude)]
    [InlineData(PromptGuidanceRung.Gemini)]
    public void Evaluate_SharedNarrationBoundary_FailsLongSilentRun(PromptGuidanceRung rung)
    {
        var acceptance = BehaviorAcceptance.Evaluate(rung, ExpectedOrder, ExpectedOrder, true, 2, 20, 0, 0);

        acceptance.Passed.ShouldBeFalse();
        acceptance.FailedChecks.ShouldContain("narrationSpacing");
        acceptance.Checks.ShouldContain(check => check.Name == "narrationSpacing" && check.Evidence == "20");
    }

    [Theory]
    [InlineData(PromptGuidanceRung.Default)]
    [InlineData(PromptGuidanceRung.Claude)]
    [InlineData(PromptGuidanceRung.Gpt)]
    [InlineData(PromptGuidanceRung.Gemini)]
    public void Evaluate_ExactlyTenCallsBetweenMessages_PassesSharedNarrationBoundary(PromptGuidanceRung rung)
    {
        var acceptance = BehaviorAcceptance.Evaluate(rung, ExpectedOrder, ExpectedOrder, true, 2, 10, 0, 0);

        acceptance.Passed.ShouldBeTrue();
        acceptance.Checks.ShouldContain(check => check.Name == "narrationSpacing" && check.Passed);
    }

    [Fact]
    public void Evaluate_GptRunWithoutNarration_FailsBecauseRequiredSequenceExceedsTenCalls()
    {
        ExpectedOrder.Length.ShouldBeGreaterThan(10);

        var acceptance = BehaviorAcceptance.Evaluate(PromptGuidanceRung.Gpt, ExpectedOrder, ExpectedOrder, true, 3, ExpectedOrder.Length, 0, 0);

        acceptance.Passed.ShouldBeFalse();
        acceptance.FailedChecks.ShouldContain("narrationSpacing");
        acceptance.Checks.ShouldContain(check => check.Name == "operationSequenceExceedsNarrationThreshold" && check.Passed);
    }

    [Fact]
    public void Evaluate_RejectedOutOfOrderOperation_FailsAcceptance()
    {
        var actual = new[] { "todo", "apply_change" }.Concat(ExpectedOrder).ToArray();

        var acceptance = BehaviorAcceptance.Evaluate(PromptGuidanceRung.Default, actual, ExpectedOrder, true, 3, 99, 0, 1);

        acceptance.Passed.ShouldBeFalse();
        acceptance.FailedChecks.ShouldContain("requiredOperationOrder");
        acceptance.FailedChecks.ShouldContain("noRejectedOperations");
        acceptance.FailedChecks.ShouldContain("narrationSpacing");
    }
}
