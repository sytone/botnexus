using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// #3064 AC1: the per-agent MRU of navigated conversations. The service itself is a plain
/// in-memory structure - "scoped to the circuit" is a DI-lifetime property, pinned separately by
/// <see cref="ConversationMruRegistrationTests"/>; these cases pin the ordering/partitioning
/// contract the redirect resolution depends on.
/// </summary>
public sealed class ConversationMruServiceTests
{
    [Fact]
    public void Most_recent_is_null_for_an_agent_that_has_never_been_recorded()
    {
        var mru = new ConversationMruService();

        Assert.Null(mru.GetMostRecent("agent-1"));
    }

    [Fact]
    public void Recording_a_conversation_makes_it_the_most_recent()
    {
        var mru = new ConversationMruService();

        mru.Record("agent-1", "c-1");

        Assert.Equal("c-1", mru.GetMostRecent("agent-1"));
    }

    [Fact]
    public void Most_recent_reflects_the_last_recorded_conversation()
    {
        var mru = new ConversationMruService();

        mru.Record("agent-1", "c-1");
        mru.Record("agent-1", "c-2");

        Assert.Equal("c-2", mru.GetMostRecent("agent-1"));
    }

    [Fact]
    public void Re_recording_an_older_conversation_promotes_it_without_duplicating()
    {
        var mru = new ConversationMruService();

        mru.Record("agent-1", "c-1");
        mru.Record("agent-1", "c-2");
        mru.Record("agent-1", "c-1");

        Assert.Equal("c-1", mru.GetMostRecent("agent-1"));
        Assert.Equal(new[] { "c-1", "c-2" }, mru.GetForAgent("agent-1"));
    }

    [Fact]
    public void The_mru_is_partitioned_per_agent()
    {
        // The whole point of "per-agent": agent-2's navigation must never become agent-1's answer.
        var mru = new ConversationMruService();

        mru.Record("agent-1", "c-1");
        mru.Record("agent-2", "c-2");

        Assert.Equal("c-1", mru.GetMostRecent("agent-1"));
        Assert.Equal("c-2", mru.GetMostRecent("agent-2"));
    }

    [Fact]
    public void Agent_and_conversation_ids_are_matched_ordinally_and_case_sensitively()
    {
        // Conversation ids are opaque server-minted tokens; case-insensitive matching would collapse
        // two genuinely distinct conversations into one MRU entry.
        var mru = new ConversationMruService();

        mru.Record("agent-1", "c-1");

        Assert.Null(mru.GetMostRecent("AGENT-1"));
        Assert.Equal(new[] { "c-1" }, mru.GetForAgent("agent-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_conversation_ids_are_not_recorded(string? conversationId)
    {
        var mru = new ConversationMruService();

        mru.Record("agent-1", conversationId!);

        Assert.Null(mru.GetMostRecent("agent-1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_agent_ids_are_not_recorded(string? agentId)
    {
        var mru = new ConversationMruService();

        mru.Record(agentId!, "c-1");

        Assert.Empty(mru.GetForAgent(agentId ?? string.Empty));
    }

    [Fact]
    public void The_mru_is_bounded_and_evicts_the_least_recently_used_entry()
    {
        // In-memory and unbounded is a leak on a long-lived circuit that fans across conversations.
        var mru = new ConversationMruService();

        for (var i = 0; i <= ConversationMruService.MaxEntriesPerAgent; i++)
            mru.Record("agent-1", $"c-{i}");

        var entries = mru.GetForAgent("agent-1");

        Assert.Equal(ConversationMruService.MaxEntriesPerAgent, entries.Count);
        Assert.Equal($"c-{ConversationMruService.MaxEntriesPerAgent}", entries[0]);
        Assert.DoesNotContain("c-0", entries);
    }

    [Fact]
    public void Removing_a_conversation_drops_it_and_exposes_the_previous_entry()
    {
        // A UI-initiated delete needs the previous entry for that agent (issue: "redirects to the
        // previous MRU entry"), so removal must not clear the whole agent's history.
        var mru = new ConversationMruService();

        mru.Record("agent-1", "c-1");
        mru.Record("agent-1", "c-2");

        mru.Remove("agent-1", "c-2");

        Assert.Equal("c-1", mru.GetMostRecent("agent-1"));
    }
}
