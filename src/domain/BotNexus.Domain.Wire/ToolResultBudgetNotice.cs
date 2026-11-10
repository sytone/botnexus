using System.Globalization;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>Authoritative counts captured when the run-local tool-result budget stops execution.</summary>
public sealed record ToolResultBudgetSignal(int Limit, long ExecutedResults, long Overshoot);

/// <summary>Shared host-owned terminal notice policy for persisted and live transcripts.</summary>
public static class ToolResultBudgetNotice
{
    /// <summary>Stable presentation kind used to identify a terminal budget notice without parsing prose.</summary>
    public const string Kind = "tool-result-budget";

    /// <summary>
    /// Returns a notice only for explicit budget stops or legacy absolute-limit evidence.
    /// Counts are optional: absent or inconsistent evidence must never imply a configured default.
    /// </summary>
    public static string? Create(this string? stopReason, bool absoluteLimitReached, ToolResultBudgetSignal? budget)
    {
        if (!absoluteLimitReached
            && !string.Equals(stopReason, "ToolResultBudget", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(stopReason, "AbsoluteLimitReached", StringComparison.OrdinalIgnoreCase))
            return null;

        const string text = "The run stopped because its tool-result budget was reached. Work is incomplete and parked. " +
            "Provide new direction to continue; no work will resume automatically.";
        if (budget is not { Limit: > 0, ExecutedResults: >= 0, Overshoot: >= 0 }
            || budget.ExecutedResults < budget.Limit
            || budget.ExecutedResults - budget.Limit != budget.Overshoot)
            return text;

        return text + string.Create(CultureInfo.InvariantCulture,
            $" Limit: {budget.Limit}. Executed results: {budget.ExecutedResults}. Overshoot: {budget.Overshoot}.");
    }
}
