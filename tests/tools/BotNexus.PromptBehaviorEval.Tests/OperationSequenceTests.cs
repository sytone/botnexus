using BotNexus.PromptBehaviorEval;

namespace BotNexus.PromptBehaviorEval.Tests;

public sealed class OperationSequenceTests
{
    [Fact]
    public void Execute_OutOfOrderOperation_IsRejectedWithoutAdvancingState()
    {
        var sequence = new OperationSequence(["inspect", "apply"]);

        var rejected = sequence.Execute("apply", () => new ToolExecution("applied"));
        var inspection = sequence.Execute("inspect", () => new ToolExecution("inspected"));
        var apply = sequence.Execute("apply", () => new ToolExecution("applied"));

        rejected.Accepted.ShouldBeFalse();
        rejected.Result.ShouldContain("expected inspect");
        inspection.Accepted.ShouldBeTrue();
        apply.Accepted.ShouldBeTrue();
    }

    [Fact]
    public void Execute_RejectedExpectedOperation_DoesNotAdvanceState()
    {
        var sequence = new OperationSequence(["inspect", "apply"]);

        var rejected = sequence.Execute("inspect", () => new ToolExecution("inspection failed", Accepted: false));
        var retried = sequence.Execute("inspect", () => new ToolExecution("inspected"));

        rejected.Accepted.ShouldBeFalse();
        retried.Accepted.ShouldBeTrue();
    }
}
