namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Audit-backed evidence that one failed tool attempt was superseded by the immediately following
/// successful invocation of the same tool.
/// </summary>
/// <param name="FailedToolCallId">The failed attempt's tool-call id.</param>
/// <param name="RecoveryToolCallId">The successful retry's tool-call id.</param>
/// <param name="ToolName">The tool shared by the adjacent attempts.</param>
public sealed record SubAgentRecoveredToolFailure(
    string FailedToolCallId,
    string RecoveryToolCallId,
    string ToolName);

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
    /// Failed attempts that were immediately superseded by a successful invocation of the same
    /// tool. The ordered call ids keep recovery grounded in the measured tool timeline rather than
    /// in the child's final prose.
    /// </summary>
    public IReadOnlyList<SubAgentRecoveredToolFailure> RecoveredToolFailures { get; init; } = [];

    /// <summary>The number of failed attempts backed by an adjacent same-tool success.</summary>
    public int RecoveredToolFailureCount => RecoveredToolFailures.Count;

    /// <summary>The number of failed attempts for which no bounded recovery evidence exists.</summary>
    public int UnrecoveredToolFailureCount => FailedToolCount - RecoveredToolFailureCount;

    /// <summary>
    /// A clean outcome - no failing tools, no provider error. Used by callers that genuinely have
    /// nothing to report rather than passing <c>null</c>, so "not measured" and "measured clean"
    /// stay distinguishable.
    /// </summary>
    public static SubAgentRunOutcome Clean { get; } = new(0, null, null);

    /// <summary>
    /// Gets a value indicating whether this run contained any unresolved failure the parent must
    /// be told about - an unrecovered tool invocation, or a terminal provider error.
    /// </summary>
    public bool HasFailure => UnrecoveredToolFailureCount > 0 || !string.IsNullOrWhiteSpace(TerminalError);

    /// <summary>
    /// Gets a value indicating whether the run completed after at least one measured recovery.
    /// This is distinct from both a pristine run and a terminal failure.
    /// </summary>
    public bool HasRecoveredErrors => RecoveredToolFailureCount > 0;

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
        var recovered = new List<SubAgentRecoveredToolFailure>();

        for (var index = 0; index < response.ToolCalls.Count; index++)
        {
            var call = response.ToolCalls[index];
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

            // Recovery is deliberately narrow: only the immediately following successful call to
            // the same tool can supersede this attempt. A later or unrelated success cannot launder
            // an unresolved required operation, and final prose is never consulted.
            if (index + 1 < response.ToolCalls.Count)
            {
                var next = response.ToolCalls[index + 1];
                if (!call.IsIncomplete
                    && !next.IsError
                    && !next.IsIncomplete
                    && string.Equals(call.ToolName, next.ToolName, StringComparison.OrdinalIgnoreCase))
                {
                    recovered.Add(new SubAgentRecoveredToolFailure(
                        call.ToolCallId,
                        next.ToolCallId,
                        call.ToolName));
                }
            }
        }

        return new SubAgentRunOutcome(
            failedCount,
            lastToolError,
            string.IsNullOrWhiteSpace(response.TerminalError) ? null : response.TerminalError)
        {
            RecoveredToolFailures = recovered
        };
    }
}
