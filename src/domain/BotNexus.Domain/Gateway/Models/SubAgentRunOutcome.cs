namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// The tool-level and provider-level outcome of one sub-agent run, carried alongside the run's
/// final text across the completion boundary (issue #3565).
/// </summary>
/// <remarks>
/// <para>
/// Before #3565 the completion contract was text-only: <c>OnCompletedAsync(string subAgentId,
/// string resultSummary, ...)</c>. <c>Completed</c> versus <c>Failed</c> was decided solely by
/// whether that string was non-empty, so a run whose every tool invocation errored - but which
/// then narrated a confident summary - was recorded <c>Completed</c> and handed to the parent as
/// a normal result. The information needed to decide better existed in the child's agent loop and
/// was discarded at this boundary. The gap was in the contract, not just the branch, which is why
/// this type exists rather than an extra <c>bool</c> parameter.
/// </para>
/// <para>
/// Deliberately a <b>projection</b> of an <see cref="AgentResponse"/> rather than a second source
/// of truth: <see cref="From"/> is the only production route, so the failed-tool count the parent
/// is told about is always the one the run's own timeline reports.
/// </para>
/// </remarks>
/// <param name="FailedToolCount">
/// Number of tool invocations in the run that ended in error. Zero for a clean run.
/// </param>
/// <param name="LastToolError">
/// The error text of the LAST failing tool invocation, or <c>null</c> when none failed or the
/// failing tool produced no textual result. Last rather than first, matching the upstream
/// <c>findLast</c> analogue: the terminal failure is the one that explains why the run ended where
/// it did.
/// </param>
/// <param name="TerminalError">
/// The provider error carried by the run's terminal assistant message, or <c>null</c> when the
/// message ended normally.
/// </param>
public sealed record SubAgentRunOutcome(
    int FailedToolCount,
    string? LastToolError,
    string? TerminalError)
{
    /// <summary>
    /// A clean outcome - no failing tools, no provider error. Used by callers that genuinely have
    /// nothing to report rather than passing <c>null</c>, so "not measured" and "measured clean"
    /// stay distinguishable.
    /// </summary>
    public static SubAgentRunOutcome Clean { get; } = new(0, null, null);

    /// <summary>
    /// Gets a value indicating whether this run contained any failure the parent must be told
    /// about - a failing tool invocation, or a terminal provider error.
    /// </summary>
    public bool HasFailure => FailedToolCount > 0 || !string.IsNullOrWhiteSpace(TerminalError);

    /// <summary>
    /// Projects a completed blocking run into its outcome. The only production route to an
    /// instance built from a run.
    /// </summary>
    /// <param name="response">The completed blocking run.</param>
    /// <returns>The run's tool/provider outcome.</returns>
    public static SubAgentRunOutcome From(AgentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var failedCount = 0;
        string? lastToolError = null;

        foreach (var call in response.ToolCalls)
        {
            if (!call.IsError)
                continue;

            failedCount++;

            // A failing tool with no textual result must still COUNT - the count is what forces
            // the failed classification - but it must not overwrite a previous, informative error
            // string with null. Otherwise the diagnostic handed to the parent degrades to "a tool
            // failed" precisely when the run failed most.
            var error = string.IsNullOrWhiteSpace(call.ResultContent)
                ? $"tool '{call.ToolName}' failed without producing an error message"
                : call.ResultContent;

            lastToolError = error;
        }

        return new SubAgentRunOutcome(
            failedCount,
            lastToolError,
            string.IsNullOrWhiteSpace(response.TerminalError) ? null : response.TerminalError);
    }
}
