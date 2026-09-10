namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

/// <summary>How a step ended, or that it has not.</summary>
public enum RunStepStatus
{
    /// <summary>Started, still running right now.</summary>
    Running,

    /// <summary>Finished and reported success.</summary>
    Succeeded,

    /// <summary>Finished and reported an error.</summary>
    Failed,

    /// <summary>
    /// Started and never reported anything. Not an error the agent saw - the run stopped
    /// underneath it (crash, restart, kill), so nothing ever wrote a result row.
    /// </summary>
    Unfinished
}

/// <summary>
/// One step in a run: a single tool call, with how long it took and how it ended.
/// </summary>
/// <param name="ToolCallId">Correlation id, and the key the transcript rows were grouped on.</param>
/// <param name="ToolName">Raw tool name. The display label is the caller's business.</param>
/// <param name="ToolArgs">Serialised arguments, when a start row carried them.</param>
/// <param name="StartedAt">When the call began.</param>
/// <param name="Duration">
/// How long it ran. Null only when a step is <see cref="RunStepStatus.Unfinished"/> and the
/// transcript gives nothing to measure against.
/// </param>
/// <param name="Status">How it ended, or that it did not.</param>
public sealed record RunStep(
    string ToolCallId,
    string ToolName,
    string? ToolArgs,
    DateTimeOffset StartedAt,
    TimeSpan? Duration,
    RunStepStatus Status);

/// <summary>
/// Turns a conversation transcript into an ordered list of steps.
///
/// <remarks>
/// Derived rather than stored, and deliberately so: BotNexus runs unattended cron and webhook
/// work that nobody watches, and the case this serves is reviewing such a run afterwards. A
/// timeline assembled from live SignalR events would be empty for exactly the runs that need it.
/// Everything here comes from rows the gateway already persists.
///
/// <para>
/// The transcript arrives in two different shapes and both have to work:
/// </para>
/// <list type="bullet">
/// <item>
/// LIVE - one message per call. <c>HandleToolEnd</c> rewrites the start message in place and
/// stamps <c>ToolDuration</c>, leaving <c>Timestamp</c> at the START time. Subtracting
/// timestamps here would yield zero for every step; the stamped duration is the only truth.
/// </item>
/// <item>
/// RELOADED - two messages per call, a tool-start row and a tool-result row, each with its own
/// server timestamp and neither with a duration (the client computes durations and never
/// persists them). Here the timestamps ARE the truth.
/// </item>
/// </list>
/// <para>
/// So duration prefers a stamped <c>ToolDuration</c> and falls back to the timestamp spread.
/// Grouping by tool-call id absorbs the one-row/two-row difference on its own.
/// </para>
/// </remarks>
/// </summary>
public static class RunTimeline
{
    /// <summary>
    /// Builds the step list for a conversation.
    /// </summary>
    /// <param name="messages">The conversation transcript, in any order.</param>
    /// <param name="activeToolCalls">
    /// Calls in flight right now, from <c>ConversationStreamState.ActiveToolCalls</c>. A call
    /// listed here is <see cref="RunStepStatus.Running"/> whatever the transcript says.
    /// </param>
    /// <param name="now">Clock, so a running step's elapsed time is testable.</param>
    /// <returns>Steps oldest first - the order they happened in, which is the order they read in.</returns>
    public static IReadOnlyList<RunStep> Build(
        IReadOnlyList<ChatMessage>? messages,
        IReadOnlyDictionary<string, ActiveToolCall>? activeToolCalls,
        DateTimeOffset now)
    {
        var live = activeToolCalls ?? new Dictionary<string, ActiveToolCall>();
        var steps = new List<RunStep>();

        var groups = (messages ?? [])
            .Where(m => m.IsToolCall && !string.IsNullOrEmpty(m.ToolCallId))
            .GroupBy(m => m.ToolCallId!);

        foreach (var group in groups)
        {
            var rows = group.OrderBy(m => m.Timestamp).ToList();
            var startedAt = rows[0].Timestamp;

            // A tool name can be absent on one row of a pair; take the first that has one rather
            // than letting an unnamed result row blank the step.
            var toolName = rows.Select(r => r.ToolName).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                           ?? "tool";

            // Arguments only ever ride the start row.
            var toolArgs = rows.Select(r => r.ToolArgs).FirstOrDefault(a => !string.IsNullOrEmpty(a));

            if (live.ContainsKey(group.Key))
            {
                // In flight. Measure from the ActiveToolCall's own StartedAt where we have it -
                // the message timestamp is the same instant, but the tracked value is the one the
                // handler will subtract from when the call ends, so they agree by construction.
                var runningSince = live[group.Key].StartedAt;
                steps.Add(new RunStep(
                    group.Key, toolName, toolArgs, runningSince,
                    Max(now - runningSince, TimeSpan.Zero),
                    RunStepStatus.Running));
                continue;
            }

            var stamped = rows.Select(r => r.ToolDuration).FirstOrDefault(d => d is not null);
            var spread = rows[^1].Timestamp - startedAt;

            // What makes a step finished, in the order these signals are trustworthy:
            //
            //   - a stamped duration   the live path measured it
            //   - a second row         the reload path has a distinct tool-result row
            //   - an error flag        something reported an outcome, and it was a bad one
            //   - a non-empty result   ditto, and a good one
            //
            // The last two matter more than they look. The client projects EVERY tool row's
            // content into ToolResult, so a lone row that carries an outcome but no measured
            // duration is a finished call - and calling it Unfinished would report a tool that
            // failed loudly as a run that died silently, which sends a reviewer looking in
            // completely the wrong place. An empty ToolResult is not evidence either way: the
            // start row of a reloaded pair has one.
            var finished = stamped is not null
                || rows.Count > 1
                || rows.Any(r => r.ToolIsError == true)
                || rows.Any(r => !string.IsNullOrEmpty(r.ToolResult));

            if (!finished)
            {
                steps.Add(new RunStep(
                    group.Key, toolName, toolArgs, startedAt, null, RunStepStatus.Unfinished));
                continue;
            }

            steps.Add(new RunStep(
                group.Key,
                toolName,
                toolArgs,
                startedAt,
                stamped ?? spread,
                rows.Any(r => r.ToolIsError == true) ? RunStepStatus.Failed : RunStepStatus.Succeeded));
        }

        return [.. steps.OrderBy(s => s.StartedAt).ThenBy(s => s.ToolCallId, StringComparer.Ordinal)];
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
