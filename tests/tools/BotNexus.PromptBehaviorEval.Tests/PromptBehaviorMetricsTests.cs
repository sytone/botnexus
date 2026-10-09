using BotNexus.PromptBehaviorEval;

namespace BotNexus.PromptBehaviorEval.Tests;

public sealed class PromptBehaviorMetricsTests
{
    [Fact]
    public void Compute_ObservedRun_RecordsOrderTransitionsAndMaximumSilentSpacing()
    {
        BehaviorObservation[] observations =
        [
            BehaviorObservation.Assistant("starting"),
            BehaviorObservation.Tool("todo", "pending->in_progress"),
            BehaviorObservation.Tool("inspect"),
            BehaviorObservation.Tool("change"),
            BehaviorObservation.Assistant("checkpoint"),
            BehaviorObservation.Tool("todo", "in_progress->done"),
            BehaviorObservation.Tool("verify"),
            BehaviorObservation.Assistant("finished"),
        ];
        var result = PromptBehaviorMetrics.Compute(observations);
        result.ToolOrder.ShouldBe(["todo", "inspect", "change", "todo", "verify"]);
        result.TodoTransitions.ShouldBe(["pending->in_progress", "in_progress->done"]);
        result.AssistantMessageCount.ShouldBe(3);
        result.MaximumSilentToolCallSpacing.ShouldBe(3);
        result.RejectedOperationCount.ShouldBe(0);
    }

    [Fact]
    public void Compute_EmptyToolCallAssistantCountsButDoesNotResetSilentSpacing()
    {
        BehaviorObservation[] observations =
        [
            BehaviorObservation.Assistant("starting"),
            BehaviorObservation.Tool("one"),
            BehaviorObservation.Assistant(string.Empty),
            BehaviorObservation.Tool("two"),
            BehaviorObservation.Assistant("checkpoint"),
        ];

        var result = PromptBehaviorMetrics.Compute(observations);

        result.AssistantMessageCount.ShouldBe(3);
        result.MaximumSilentToolCallSpacing.ShouldBe(2);
    }

    [Fact]
    public void Compute_TrailingTools_CountTowardMaximumSilentSpacing()
    {
        BehaviorObservation[] observations =
        [
            BehaviorObservation.Assistant("starting"),
            BehaviorObservation.Tool("one"),
            BehaviorObservation.Tool("two"),
        ];
        PromptBehaviorMetrics.Compute(observations).MaximumSilentToolCallSpacing.ShouldBe(2);
    }
}
