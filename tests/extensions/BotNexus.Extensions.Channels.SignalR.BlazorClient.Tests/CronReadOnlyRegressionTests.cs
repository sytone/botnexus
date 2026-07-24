using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Behavioural regression guards for the ONE thing epic #2300 could silently break while deleting
/// the mutable virtual-session flag (issue #2305): <b>cron conversations must stay read-only</b>.
///
/// <para>
/// The mechanism changed — read-only used to OR in a mutable client flag, and now derives purely
/// from the immutable, server-stamped <see cref="ConversationSource"/> — so these tests assert the
/// BEHAVIOUR rather than the implementation. They also pin the two neighbouring behaviours that the
/// same change could have flipped in either direction: sub-agent observer views must stay
/// read-only, and a user's own channel conversation must stay WRITABLE even while cron and
/// sub-agent events are being dispatched against the same agent.
/// </para>
/// </summary>
public sealed class CronReadOnlyRegressionTests
{
    private static ConversationState Conversation(
        ConversationSource source = ConversationSource.Channel,
        ConversationKind kind = ConversationKind.HumanAgent) =>
        new()
        {
            ConversationId = "c-1",
            Title = "Conversation",
            Status = "Active",
            Source = source,
            Kind = kind
        };

    // ── Cron stays read-only ────────────────────────────────────────────────────

    /// <summary>
    /// The headline guard. A cron-originated conversation is read-only and shows no composer under
    /// EVERY selection source — including a deliberate user click, which is the path a user would
    /// take to try to type into it.
    /// </summary>
    [Theory]
    [InlineData(SelectionSource.UserClick)]
    [InlineData(SelectionSource.RouteNavigation)]
    [InlineData(SelectionSource.SubAgentView)]
    [InlineData(SelectionSource.Bootstrap)]
    public void CronConversation_IsReadOnly_UnderEverySelectionSource(SelectionSource selection)
    {
        var projection = Conversation(ConversationSource.Cron).Project(selection);

        Assert.True(projection.IsUnattended);
        Assert.True(
            projection.IsReadOnly,
            "Cron conversations must remain read-only. The mechanism moved from the deleted mutable " +
            "virtual-session flag to Source == ConversationSource.Cron; if this fails, cron " +
            "conversations just became writable.");
        Assert.False(projection.ShowComposer);
    }

    /// <summary>
    /// Cron read-only survives the full server round-trip: the DTO's <c>source="Cron"</c> is parsed
    /// into the immutable typed field by the store, and the projection reads it back.
    /// </summary>
    [Fact]
    public void CronConversation_SeededFromServerPayload_IsReadOnly()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-cron", "a-1", "Nightly digest", false, "Active", null, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "HumanAgent", "Cron")
        ]);

        var conversation = store.GetAgent("a-1")!.Conversations["c-cron"];

        Assert.Equal(ConversationSource.Cron, conversation.Source);
        Assert.True(conversation.Project(SelectionSource.UserClick).IsReadOnly);
    }

    /// <summary>
    /// Webhook runs are unattended for the same reason cron runs are: no human is in the loop.
    /// </summary>
    [Fact]
    public void WebhookConversation_IsReadOnly()
    {
        var projection = Conversation(ConversationSource.Webhook).Project(SelectionSource.UserClick);

        Assert.True(projection.IsReadOnly);
        Assert.False(projection.ShowComposer);
    }

    // ── Sub-agent observer stays read-only ──────────────────────────────────────

    /// <summary>
    /// A sub-agent supervision thread is read-only from its immutable pairing alone.
    /// </summary>
    [Fact]
    public void SubAgentConversation_IsReadOnly_FromItsImmutableKind()
    {
        var projection = Conversation(ConversationSource.Agent, ConversationKind.AgentSubAgent)
            .Project(SelectionSource.UserClick);

        Assert.True(projection.IsReadOnly);
        Assert.Equal(ConversationListGroup.AgentInitiated, projection.Group);
    }

    /// <summary>
    /// The observer-view path: even an ordinary channel conversation renders read-only while the
    /// active view was promoted by the explicit "view sub-agent" interaction (#2245/#2246).
    /// </summary>
    [Fact]
    public void ObserverView_IsReadOnly_EvenForAChannelConversation()
    {
        var projection = Conversation().Project(SelectionSource.SubAgentView);

        Assert.False(projection.IsUnattended);
        Assert.True(projection.IsReadOnly);
        Assert.False(projection.ShowComposer);
    }

    // ── User conversations stay writable ────────────────────────────────────────

    /// <summary>
    /// The inverse guard, and the reason the whole epic exists: a user's own channel conversation
    /// stays WRITABLE while inbound cron and sub-agent events are dispatched against the same
    /// agent. Every projection input is immutable, so no inbound event can reach in and hide the
    /// composer — the conversation-shaped twin of the #2248 agent-dropdown defect.
    /// </summary>
    [Fact]
    public void UserConversation_StaysWritable_WhileInboundCronAndSubAgentEventsDispatch()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-user", "a-1", "My chat", false, "Active", null, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);
        store.SelectView("a-1", "c-user", SelectionSource.UserClick);

        var conversation = store.GetAgent("a-1")!.Conversations["c-user"];
        Assert.True(conversation.Project(store.ActiveSelectionSource).ShowComposer);

        // Inbound, non-user-driven traffic against the same agent/conversation.
        store.RegisterSession("a-1", "cron:job-1:run", sessionType: "cron", conversationId: "c-user");
        store.MarkSubAgent("a-1-sub");
        store.RegisterSession("a-1", "sess-sub", sessionType: "agent-subagent", conversationId: "c-user");

        Assert.Equal(ConversationSource.Channel, conversation.Source);
        Assert.Equal(ConversationKind.HumanAgent, conversation.Kind);
        Assert.Equal(SelectionSource.UserClick, store.ActiveSelectionSource);
        Assert.False(
            conversation.Project(store.ActiveSelectionSource).IsReadOnly,
            "A user's own conversation must stay writable while cron / sub-agent events dispatch. " +
            "If this fails, an inbound event poisoned the render inputs — the exact defect class " +
            "epic #2300 deleted.");
        Assert.True(conversation.Project(store.ActiveSelectionSource).ShowComposer);
    }

    /// <summary>
    /// A cron session registered against the agent must not re-stamp the cron origin onto the
    /// user's conversation. Origin is server-stamped and <c>init</c>-only; a cron session id is not
    /// evidence about a conversation.
    /// </summary>
    [Fact]
    public void CronSessionRegistration_DoesNotMakeAUserConversationCron()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.SeedConversations("a-1", [
            new ConversationSummaryDto("c-user", "a-1", "My chat", false, "Active", null, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);

        store.RegisterSession("a-1", "cron:job-9:run", sessionType: "cron", conversationId: "c-user");

        var conversation = store.GetAgent("a-1")!.Conversations["c-user"];
        Assert.Equal(ConversationSource.Channel, conversation.Source);
        Assert.Equal(ConversationListGroup.Normal, conversation.Project(SelectionSource.UserClick).Group);
        Assert.Null(conversation.Project(SelectionSource.UserClick).Badge);
    }
}
