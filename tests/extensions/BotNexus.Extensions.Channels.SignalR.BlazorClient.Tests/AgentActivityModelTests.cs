using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Deriving what an agent is doing (Interface Review P2). State read as "○ Idle" beside a name, so
/// finding the working agent among sixteen meant reading sixteen labels.
///
/// Four states, not the comparator's six: the portal tracks a connection flag, a streaming flag,
/// in-flight tool calls and an observer classification, and nothing that separates "waiting" from
/// "blocked" or "done" from "idle". A dot for a state the data cannot support is a confident
/// rendering of a guess.
/// </summary>
public sealed class AgentActivityModelTests
{
    [Fact]
    public void A_connected_agent_with_nothing_in_flight_is_idle()
    {
        var spec = AgentActivityModel.For(isConnected: true, isStreaming: false, activeToolCalls: 0);

        spec.Activity.ShouldBe(AgentActivity.Idle);
    }

    [Fact]
    public void Idle_draws_no_indicator_because_a_dot_on_everything_distinguishes_nothing()
    {
        AgentActivityModel.For(true, false, 0).ShowsIndicator.ShouldBeFalse();
    }

    [Fact]
    public void Streaming_is_working()
    {
        var spec = AgentActivityModel.For(true, isStreaming: true, activeToolCalls: 0);

        spec.Activity.ShouldBe(AgentActivity.Working);
        spec.ShowsIndicator.ShouldBeTrue();
    }

    [Fact]
    public void Tools_outrank_streaming_because_that_is_the_part_that_takes_time()
    {
        var spec = AgentActivityModel.For(true, isStreaming: true, activeToolCalls: 2);

        spec.Activity.ShouldBe(AgentActivity.UsingTools);
    }

    [Fact]
    public void A_dropped_connection_outranks_every_activity_flag()
    {
        // While the connection is down the other flags are a stale memory of what was true when it
        // dropped. Reporting "working" off a dead connection is the reading that sends someone
        // looking for output which will never arrive.
        var spec = AgentActivityModel.For(isConnected: false, isStreaming: true, activeToolCalls: 3);

        spec.Activity.ShouldBe(AgentActivity.Offline);
    }

    [Fact]
    public void A_read_only_observer_reports_no_activity_of_its_own()
    {
        var spec = AgentActivityModel.For(true, isStreaming: true, activeToolCalls: 1, isReadOnly: true);

        spec.Activity.ShouldBe(AgentActivity.ReadOnly);
        spec.ShowsIndicator.ShouldBeFalse();
    }

    [Fact]
    public void Every_state_carries_a_label_because_colour_is_not_an_announcement()
    {
        AgentActivitySpec[] all =
        [
            AgentActivityModel.For(true, false, 0),
            AgentActivityModel.For(true, true, 0),
            AgentActivityModel.For(true, false, 1),
            AgentActivityModel.For(false, false, 0),
            AgentActivityModel.For(true, false, 0, isReadOnly: true),
        ];

        all.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.Label));
        all.ShouldAllBe(s => !string.IsNullOrWhiteSpace(s.CssModifier));
    }

    [Fact]
    public void Distinguishable_states_have_distinguishable_class_names()
    {
        // Two states sharing a modifier would render identically, which is the whole failure this
        // change exists to fix - just moved from the label to the stylesheet.
        string[] modifiers =
        [
            AgentActivityModel.For(true, false, 0).CssModifier,
            AgentActivityModel.For(true, true, 0).CssModifier,
            AgentActivityModel.For(true, false, 1).CssModifier,
            AgentActivityModel.For(false, false, 0).CssModifier,
        ];

        modifiers.Distinct(StringComparer.Ordinal).Count().ShouldBe(modifiers.Length);
    }
}
