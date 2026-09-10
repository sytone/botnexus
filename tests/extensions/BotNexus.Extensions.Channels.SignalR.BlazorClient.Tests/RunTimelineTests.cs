using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The step timeline (Interface Review P2).
///
/// The review's reason for wanting it is the constraint that shapes every test here: BotNexus
/// runs unattended cron and webhook work that nobody watches, and the timeline exists to make
/// such a run legible AFTERWARDS. So the reloaded-transcript path matters at least as much as
/// the live one, and both are covered - they carry genuinely different data and a projection
/// that handles only one of them looks correct while being useless for the actual use case.
/// </summary>
public sealed class RunTimelineTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private static ChatMessage ToolRow(
        string callId,
        string? name,
        DateTimeOffset at,
        string? args = null,
        string? result = null,
        bool isError = false,
        TimeSpan? duration = null) =>
        new("Tool", result ?? string.Empty, at)
        {
            ToolCallId = callId,
            ToolName = name,
            ToolArgs = args,
            ToolResult = result,
            ToolIsError = isError,
            ToolDuration = duration,
            IsToolCall = true
        };

    private static IReadOnlyList<RunStep> Build(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, ActiveToolCall>? live = null,
        DateTimeOffset? now = null) =>
        RunTimeline.Build(messages, live, now ?? T0.AddMinutes(10));

    // ── The reloaded transcript: two rows, server timestamps, no duration ──

    [Fact]
    public void A_reloaded_step_gets_its_duration_from_the_two_persisted_rows()
    {
        // This is the unattended-run case. The client computes durations live and never persists
        // them, so after a reload ToolDuration is null on every historical row - but the gateway
        // stamps a timestamp on the tool-start row AND the tool-result row, which is enough.
        var steps = Build([
            ToolRow("c1", "bash", T0, args: "{\"cmd\":\"ls\"}"),
            ToolRow("c1", "bash", T0.AddSeconds(42), result: "ok")
        ]);

        var step = Assert.Single(steps);
        Assert.Equal(TimeSpan.FromSeconds(42), step.Duration);
        Assert.Equal(RunStepStatus.Succeeded, step.Status);
        Assert.Equal(T0, step.StartedAt);
    }

    [Fact]
    public void The_two_rows_of_one_call_are_one_step_not_two()
    {
        // Nothing collapses the pair on the way in - every persisted row becomes its own
        // ChatMessage - so the grouping has to happen here or a reviewed run shows every step
        // twice.
        var steps = Build([
            ToolRow("c1", "bash", T0, args: "{}"),
            ToolRow("c1", "bash", T0.AddSeconds(1), result: "ok")
        ]);

        Assert.Single(steps);
    }

    [Fact]
    public void Arguments_survive_from_the_start_row()
    {
        // Only the start row carries them, and it is not the last row in the group.
        var steps = Build([
            ToolRow("c1", "bash", T0, args: "{\"cmd\":\"ls\"}"),
            ToolRow("c1", "bash", T0.AddSeconds(1), result: "ok")
        ]);

        Assert.Equal("{\"cmd\":\"ls\"}", Assert.Single(steps).ToolArgs);
    }

    [Fact]
    public void A_result_row_without_a_tool_name_does_not_blank_the_step()
    {
        var steps = Build([
            ToolRow("c1", "bash", T0, args: "{}"),
            ToolRow("c1", null, T0.AddSeconds(1), result: "ok")
        ]);

        Assert.Equal("bash", Assert.Single(steps).ToolName);
    }

    // ── The live transcript: ONE row, rewritten in place ──────────────────

    [Fact]
    public void A_live_step_uses_its_stamped_duration_rather_than_its_timestamps()
    {
        // HandleToolEnd rewrites the start message in place and stamps ToolDuration, leaving
        // Timestamp at the START time. Subtracting timestamps here would report zero seconds for
        // every step of a run you actually watched - the worst kind of wrong, because it looks
        // like a real measurement.
        var steps = Build([
            ToolRow("c1", "bash", T0, args: "{}", result: "ok", duration: TimeSpan.FromSeconds(9))
        ]);

        var step = Assert.Single(steps);
        Assert.Equal(TimeSpan.FromSeconds(9), step.Duration);
        Assert.Equal(RunStepStatus.Succeeded, step.Status);
    }

    [Fact]
    public void A_stamped_duration_wins_over_the_timestamp_spread()
    {
        // Both signals present and disagreeing: the stamp was measured, the spread is an artefact.
        var steps = Build([
            ToolRow("c1", "bash", T0, args: "{}", duration: TimeSpan.FromSeconds(9)),
            ToolRow("c1", "bash", T0.AddHours(3), result: "ok")
        ]);

        Assert.Equal(TimeSpan.FromSeconds(9), Assert.Single(steps).Duration);
    }

    // ── Steps that never finished, which is the point of reviewing a run ──

    [Fact]
    public void A_step_that_started_and_never_reported_is_marked_unfinished()
    {
        // 62 of these in the live database. The run stopped underneath the call - crash, restart,
        // kill - so nothing ever wrote a result row. Reporting it as succeeded, or dropping it,
        // hides the single most useful fact about a failed unattended run.
        var steps = Build([ToolRow("c1", "bash", T0, args: "{}")]);

        var step = Assert.Single(steps);
        Assert.Equal(RunStepStatus.Unfinished, step.Status);
        Assert.Null(step.Duration);
    }

    [Fact]
    public void A_lone_row_that_reported_an_error_is_failed_not_unfinished()
    {
        // Caught by the panel tests, not these: a single row carrying an outcome but no measured
        // duration was being read as "the run died here". That reports a tool which failed loudly
        // as a gateway that stopped silently, and sends a reviewer looking in the wrong place.
        var steps = Build([ToolRow("c1", "bash", T0, args: "{}", result: "boom", isError: true)]);

        Assert.Equal(RunStepStatus.Failed, Assert.Single(steps).Status);
    }

    [Fact]
    public void A_lone_row_that_reported_a_result_is_finished()
    {
        var steps = Build([ToolRow("c1", "bash", T0, args: "{}", result: "ok")]);

        Assert.Equal(RunStepStatus.Succeeded, Assert.Single(steps).Status);
    }

    [Fact]
    public void An_empty_result_string_is_not_evidence_that_a_step_finished()
    {
        // The client projects every tool row's content into ToolResult, so the START row of a
        // reloaded pair carries an empty one. Treating that as an outcome would mark every
        // abandoned step as a success.
        var steps = Build([ToolRow("c1", "bash", T0, args: "{}", result: string.Empty)]);

        Assert.Equal(RunStepStatus.Unfinished, Assert.Single(steps).Status);
    }
    [Fact]
    public void An_unfinished_step_is_not_confused_with_a_running_one()
    {
        // Same transcript shape - one row, no result. The difference is whether the call is in
        // flight RIGHT NOW, which only the live dictionary knows.
        var messages = new[] { ToolRow("c1", "bash", T0, args: "{}") };

        var abandoned = Build(messages);
        var running = Build(messages, Live("c1", "bash", T0));

        Assert.Equal(RunStepStatus.Unfinished, Assert.Single(abandoned).Status);
        Assert.Equal(RunStepStatus.Running, Assert.Single(running).Status);
    }

    [Fact]
    public void A_running_step_reports_how_long_it_has_been_running()
    {
        var steps = Build(
            [ToolRow("c1", "bash", T0, args: "{}")],
            Live("c1", "bash", T0),
            now: T0.AddSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(steps).Duration);
    }

    [Fact]
    public void A_clock_that_runs_backwards_does_not_produce_a_negative_duration()
    {
        // Client clock vs server timestamps; a negative elapsed time renders as nonsense.
        var steps = Build(
            [ToolRow("c1", "bash", T0, args: "{}")],
            Live("c1", "bash", T0),
            now: T0.AddSeconds(-5));

        Assert.Equal(TimeSpan.Zero, Assert.Single(steps).Duration);
    }

    // ── Failure ───────────────────────────────────────────────────────────

    [Fact]
    public void A_step_that_reported_an_error_is_marked_failed()
    {
        var steps = Build([
            ToolRow("c1", "bash", T0, args: "{}"),
            ToolRow("c1", "bash", T0.AddSeconds(2), result: "boom", isError: true)
        ]);

        Assert.Equal(RunStepStatus.Failed, Assert.Single(steps).Status);
    }

    [Fact]
    public void An_error_on_either_row_of_a_pair_fails_the_step()
    {
        // The flag rides the result row live and can ride either after a reload; a step is failed
        // if anything about it said so.
        var steps = Build([
            ToolRow("c1", "bash", T0, args: "{}", isError: true),
            ToolRow("c1", "bash", T0.AddSeconds(2), result: "boom")
        ]);

        Assert.Equal(RunStepStatus.Failed, Assert.Single(steps).Status);
    }

    // ── Ordering and exclusion ────────────────────────────────────────────

    [Fact]
    public void Steps_read_in_the_order_they_happened()
    {
        var steps = Build([
            ToolRow("c2", "write", T0.AddSeconds(30), args: "{}", duration: TimeSpan.FromSeconds(1)),
            ToolRow("c1", "read", T0, args: "{}", duration: TimeSpan.FromSeconds(1)),
            ToolRow("c3", "bash", T0.AddSeconds(60), args: "{}", duration: TimeSpan.FromSeconds(1))
        ]);

        Assert.Equal(["read", "write", "bash"], steps.Select(s => s.ToolName));
    }

    [Fact]
    public void Ordering_is_stable_when_two_steps_share_a_timestamp()
    {
        // Parallel tool calls land on the same instant. Without a tie-break the list would
        // reshuffle between renders, which reads as flicker.
        var messages = new[]
        {
            ToolRow("c-b", "write", T0, args: "{}", duration: TimeSpan.FromSeconds(1)),
            ToolRow("c-a", "read", T0, args: "{}", duration: TimeSpan.FromSeconds(1))
        };

        Assert.Equal(
            Build(messages).Select(s => s.ToolCallId),
            Build([.. messages.Reverse()]).Select(s => s.ToolCallId));
    }

    [Fact]
    public void Ordinary_conversation_messages_are_not_steps()
    {
        var steps = Build([
            new ChatMessage("User", "run the thing", T0),
            new ChatMessage("Assistant", "on it", T0.AddSeconds(1)),
            ToolRow("c1", "bash", T0.AddSeconds(2), args: "{}", duration: TimeSpan.FromSeconds(1))
        ]);

        Assert.Equal("bash", Assert.Single(steps).ToolName);
    }

    [Fact]
    public void A_tool_row_with_no_call_id_is_skipped_rather_than_grouped_with_others()
    {
        // Grouping on a null key would merge every unidentifiable row into one bogus step.
        var steps = Build([
            ToolRow(string.Empty, "bash", T0, args: "{}"),
            ToolRow("c1", "read", T0.AddSeconds(1), args: "{}", duration: TimeSpan.FromSeconds(1))
        ]);

        Assert.Equal("read", Assert.Single(steps).ToolName);
    }

    [Fact]
    public void An_empty_or_absent_transcript_produces_no_steps()
    {
        Assert.Empty(RunTimeline.Build(null, null, T0));
        Assert.Empty(Build([]));
    }

    private static Dictionary<string, ActiveToolCall> Live(string id, string name, DateTimeOffset at) =>
        new()
        {
            [id] = new ActiveToolCall
            {
                ToolCallId = id,
                ToolName = name,
                StartedAt = at,
                MessageId = "m-" + id
            }
        };
}
