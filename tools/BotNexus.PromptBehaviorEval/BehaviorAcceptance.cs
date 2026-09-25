namespace BotNexus.PromptBehaviorEval;

/// <summary>One named acceptance decision emitted in result JSON.</summary>
public sealed record AcceptanceCheck(string Name, bool Passed, string Evidence);

/// <summary>Explicit pass/fail evaluation of the issue's observed behavior requirements.</summary>
public sealed record BehaviorAcceptance(
    bool Passed,
    IReadOnlyList<string> FailedChecks,
    IReadOnlyList<string> ExpectedOperationOrder,
    IReadOnlyList<AcceptanceCheck> Checks)
{
    /// <summary>Evaluates common checklist/order requirements and the GPT-only narration maximum.</summary>
    public static BehaviorAcceptance Evaluate(
        PromptGuidanceRung rung,
        IReadOnlyList<string> actualOrder,
        IReadOnlyList<string> expectedOrder,
        bool addedDiscoveredItemAfterInspection,
        int distinctDoneItemCount,
        int maximumSilentToolCallSpacing,
        int rejectedTodoTransitionCount,
        int rejectedOperationCount)
    {
        var checks = new List<AcceptanceCheck>
        {
            new("requiredOperationOrder", actualOrder.SequenceEqual(expectedOrder), string.Join(" -> ", actualOrder)),
            new("operationSequenceExceedsNarrationThreshold", expectedOrder.Count > 10, expectedOrder.Count.ToString()),
            new("discoveredItemAddedAfterInspection", addedDiscoveredItemAfterInspection, addedDiscoveredItemAfterInspection.ToString()),
            new("atLeastTwoDistinctDoneItems", distinctDoneItemCount >= 2, distinctDoneItemCount.ToString()),
            new("noRejectedTodoTransitions", rejectedTodoTransitionCount == 0, rejectedTodoTransitionCount.ToString()),
            new("noRejectedOperations", rejectedOperationCount == 0, rejectedOperationCount.ToString()),
        };
        if (rung == PromptGuidanceRung.Gpt)
            checks.Add(new("gptNarrationSpacing", maximumSilentToolCallSpacing <= 10, maximumSilentToolCallSpacing.ToString()));

        var failed = checks.Where(check => !check.Passed).Select(check => check.Name).ToArray();
        return new BehaviorAcceptance(failed.Length == 0, failed, expectedOrder.ToArray(), checks);
    }
}
