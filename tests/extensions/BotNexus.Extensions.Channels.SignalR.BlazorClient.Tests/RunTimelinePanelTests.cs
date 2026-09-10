using Bunit;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;
using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// The Steps tab's rendering. <see cref="RunTimelineTests"/> covers the projection; this covers
/// what a reviewer actually sees, including the two things the panel exists to make obvious - a
/// step that ate the run, and a step that never came back.
/// </summary>
public sealed class RunTimelinePanelTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private readonly BunitContext _ctx = new();
    private readonly ClientStateStore _store = new();

    public RunTimelinePanelTests()
    {
        _ctx.Services.AddSingleton<IClientStateStore>(_store);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    public void Dispose() => _ctx.Dispose();

    private ConversationState Seed(string agentId = "gantry-manager", string conversationId = "c-1")
    {
        _store.UpsertAgent(new AgentState
        {
            AgentId = agentId,
            DisplayName = "Gantry Manager",
            IsConnected = true
        });

        var conversation = new ConversationState { ConversationId = conversationId };
        _store.GetAgent(agentId)!.Conversations[conversationId] = conversation;
        _store.SelectView(agentId, conversationId, SelectionSource.UserClick);
        return conversation;
    }

    private IRenderedComponent<RunTimelinePanel> Render(
        string agentId = "gantry-manager",
        string conversationId = "c-1",
        DateTimeOffset? now = null) =>
        _ctx.Render<RunTimelinePanel>(p => p
            .Add(c => c.AgentId, agentId)
            .Add(c => c.ConversationId, conversationId)
            .Add(c => c.Now, () => now ?? T0.AddMinutes(5)));

    private static ChatMessage ToolRow(
        string callId, string name, DateTimeOffset at,
        string? args = null, string? result = null,
        bool isError = false, TimeSpan? duration = null) =>
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

    // ── Nothing to show ───────────────────────────────────────────────────

    [Fact]
    public void A_conversation_with_no_tool_calls_says_so_instead_of_rendering_an_empty_list()
    {
        Seed();

        var cut = Render();

        Assert.NotNull(cut.Find("[data-testid=run-timeline-empty]"));
        Assert.Empty(cut.FindAll("[data-testid=run-timeline-step]"));
    }

    [Fact]
    public void An_agent_that_does_not_exist_yet_renders_without_throwing()
    {
        // The panel can mount before the roster arrives.
        var cut = Render(agentId: "not-loaded-yet");

        Assert.NotNull(cut.Find("[data-testid=run-timeline-empty]"));
    }

    // ── The two facts the panel exists to surface ─────────────────────────

    [Fact]
    public void A_step_that_never_finished_is_counted_separately_from_a_failure()
    {
        // Nothing reported an error - the run stopped underneath the call. Folding it into
        // "failed" would send a reviewer looking for a bug in a tool that never misbehaved.
        var conversation = Seed();
        conversation.AppendMessage(ToolRow("c1", "bash", T0, args: "{}"));
        conversation.AppendMessage(ToolRow("c2", "read", T0.AddSeconds(1), args: "{}", result: "boom", isError: true));

        var cut = Render();

        Assert.Contains("1 never finished", cut.Find("[data-testid=run-timeline-unfinished-count]").TextContent);
        Assert.Contains("1 failed", cut.Find("[data-testid=run-timeline-failed-count]").TextContent);
    }

    [Fact]
    public void The_step_that_ate_the_run_is_the_widest_bar()
    {
        // The whole point of a bar rather than a column of numbers. Scaled to the slowest step in
        // the conversation, because run lengths here span three orders of magnitude and a fixed
        // ceiling would render every ordinary step as a nub.
        var conversation = Seed();
        conversation.AppendMessage(ToolRow("c1", "read", T0, args: "{}", duration: TimeSpan.FromSeconds(1)));
        conversation.AppendMessage(ToolRow("c2", "bash", T0.AddSeconds(2), args: "{}", duration: TimeSpan.FromSeconds(100)));

        var cut = Render();

        var bars = cut.FindAll(".run-timeline-bar");
        Assert.Equal(2, bars.Count);
        Assert.Contains("width:100%", bars[1].GetAttribute("style"));
        // The fast one still has to be visible, or "it ran" is indistinguishable from "it didn't".
        var fast = bars[0].GetAttribute("style")!;
        Assert.DoesNotContain("width:0", fast);
    }

    // ── What each row says ────────────────────────────────────────────────

    [Fact]
    public void Each_step_is_labelled_by_what_it_did_not_just_which_tool_ran()
    {
        // Reuses the transcript's own formatter, so a step reads the same in both places.
        var conversation = Seed();
        conversation.AppendMessage(ToolRow(
            "c1", "read", T0,
            args: "{\"path\":\"/srv/gantry/Program.cs\"}",
            duration: TimeSpan.FromSeconds(1)));

        var cut = Render();

        Assert.Contains("Program.cs", cut.Find(".run-timeline-label").TextContent);
    }

    [Fact]
    public void A_running_step_shows_elapsed_time_rather_than_a_dash()
    {
        var conversation = Seed();
        conversation.AppendMessage(ToolRow("c1", "bash", T0, args: "{}"));
        conversation.StreamState.ActiveToolCalls["c1"] = new ActiveToolCall
        {
            ToolCallId = "c1",
            ToolName = "bash",
            StartedAt = T0,
            MessageId = "m-1"
        };

        var cut = Render(now: T0.AddSeconds(30));

        var step = cut.Find("[data-testid=run-timeline-step]");
        Assert.Equal("running", step.GetAttribute("data-status"));
        Assert.Contains("30s", cut.Find("[data-testid=run-timeline-duration]").TextContent);
    }

    [Fact]
    public void A_step_with_no_measurable_duration_shows_a_dash_rather_than_zero()
    {
        // "0ms" is a claim that it ran instantly. It didn't - nobody knows how long it ran.
        var conversation = Seed();
        conversation.AppendMessage(ToolRow("c1", "bash", T0, args: "{}"));

        var cut = Render();

        Assert.Equal("—", cut.Find("[data-testid=run-timeline-duration]").TextContent.Trim());
    }

    [Fact]
    public void Status_is_announced_rather_than_left_to_the_marker_colour()
    {
        // The marker is a coloured dot. Colour alone is not a signal.
        var conversation = Seed();
        conversation.AppendMessage(ToolRow("c1", "bash", T0, args: "{}", result: "boom", isError: true));

        var cut = Render();

        Assert.Contains("failed", cut.Find(".run-timeline-step .sr-only").TextContent);
    }

    [Fact]
    public void Steps_appear_in_the_order_they_ran()
    {
        var conversation = Seed();
        conversation.AppendMessage(ToolRow("c2", "write", T0.AddSeconds(30), args: "{}", duration: TimeSpan.FromSeconds(1)));
        conversation.AppendMessage(ToolRow("c1", "read", T0, args: "{}", duration: TimeSpan.FromSeconds(1)));

        var cut = Render();

        var tools = cut.FindAll("[data-testid=run-timeline-step]")
            .Select(e => e.GetAttribute("data-tool"))
            .ToList();
        Assert.Equal(["read", "write"], tools);
    }

    // ── Making a long run legible ─────────────────────────────────────────

    [Fact]
    public void A_long_run_can_be_cut_down_to_just_what_went_wrong()
    {
        // The busiest conversation on the live instance has 194 steps and 12 failures. Scrolling
        // 194 rows to find 12 is the problem the timeline was meant to solve.
        var conversation = Seed();
        for (var i = 0; i < 20; i++)
        {
            conversation.AppendMessage(ToolRow(
                $"ok-{i}", "bash", T0.AddSeconds(i), args: "{}", result: "fine",
                duration: TimeSpan.FromSeconds(1)));
        }
        conversation.AppendMessage(ToolRow("bad", "bash", T0.AddSeconds(30), args: "{}", result: "boom", isError: true));
        conversation.AppendMessage(ToolRow("gone", "bash", T0.AddSeconds(40), args: "{}"));

        var cut = Render();
        Assert.Equal(22, cut.FindAll("[data-testid=run-timeline-step]").Count);

        cut.Find("[data-testid=run-timeline-filter]").Click();

        var shown = cut.FindAll("[data-testid=run-timeline-step]");
        Assert.Equal(2, shown.Count);
        Assert.Equal(["failed", "unfinished"], shown.Select(e => e.GetAttribute("data-status")));
    }

    [Fact]
    public void Filtering_does_not_change_the_totals_it_is_filtering_by()
    {
        // The summary counts every step. A filtered view that also rewrote the headline would
        // make the numbers disagree with themselves - "1 failed" above a list of one row is no
        // longer telling you anything.
        var conversation = Seed();
        conversation.AppendMessage(ToolRow("ok", "bash", T0, args: "{}", result: "fine", duration: TimeSpan.FromSeconds(1)));
        conversation.AppendMessage(ToolRow("bad", "bash", T0.AddSeconds(1), args: "{}", result: "boom", isError: true));

        var cut = Render();
        // The stats, not the whole summary bar - the filter button lives inside it and its own
        // label is supposed to change.
        string Stats() => string.Concat(cut.FindAll(".run-timeline-stat").Select(e => e.TextContent));

        var before = Stats();

        cut.Find("[data-testid=run-timeline-filter]").Click();

        Assert.Equal(before, Stats());
        Assert.Contains("2 steps", before);
        Assert.Contains("1 failed", before);
    }

    [Fact]
    public void A_run_with_nothing_wrong_is_offered_no_filter()
    {
        // A control that would empty the list is worse than no control.
        var conversation = Seed();
        conversation.AppendMessage(ToolRow("ok", "bash", T0, args: "{}", result: "fine", duration: TimeSpan.FromSeconds(1)));

        var cut = Render();

        Assert.Empty(cut.FindAll("[data-testid=run-timeline-filter]"));
    }
    // ── It has to survive a reload, which is the actual use case ──────────

    [Fact]
    public void A_reloaded_run_still_reports_durations()
    {
        // The client computes durations live and never persists them, so every rehydrated row has
        // a null ToolDuration. This is the unattended-cron case the review asked for, and a panel
        // that only worked while you watched would miss it entirely.
        var conversation = Seed();
        conversation.AppendMessage(ToolRow("c1", "bash", T0, args: "{}"));
        conversation.AppendMessage(ToolRow("c1", "bash", T0.AddSeconds(42), result: "ok"));

        var cut = Render();

        Assert.Single(cut.FindAll("[data-testid=run-timeline-step]"));
        Assert.Contains("42s", cut.Find("[data-testid=run-timeline-duration]").TextContent);
    }
}
