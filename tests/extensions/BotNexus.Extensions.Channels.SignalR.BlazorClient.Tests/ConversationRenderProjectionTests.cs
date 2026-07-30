using BotNexus.Extensions.Channels.SignalR.BlazorClient.Services;

namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Tests;

/// <summary>
/// Truth-table and immutability coverage for the client-side conversation origin signal introduced
/// by issue #2304 (epic #2300, slice D): <see cref="ConversationState.Source"/> /
/// <see cref="ConversationState.Kind"/> and the deterministic
/// <see cref="ConversationRenderProjection"/> derived from them.
/// </summary>
public sealed class ConversationRenderProjectionTests
{
    private static ConversationSummaryDto Dto(
        string id,
        string kind = "HumanAgent",
        string source = "Channel") =>
        new(id, "a-1", $"Title {id}", false, "Active", null, 0,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, kind, source);

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Channel", ConversationSource.Channel)]
    [InlineData("channel", ConversationSource.Channel)]
    [InlineData("Cron", ConversationSource.Cron)]
    [InlineData("cron", ConversationSource.Cron)]
    [InlineData("Webhook", ConversationSource.Webhook)]
    [InlineData("Agent", ConversationSource.Agent)]
    public void ParseSource_ParsesEveryWireValue_CaseInsensitively(string wire, ConversationSource expected) =>
        Assert.Equal(expected, ConversationOrigin.ParseSource(wire));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SomethingNewerServerSent")]
    [InlineData("7")]
    public void ParseSource_FallsBackToChannel_ForUnknownOrAbsentValues(string? wire) =>
        Assert.Equal(ConversationSource.Channel, ConversationOrigin.ParseSource(wire));

    [Theory]
    [InlineData("HumanAgent", ConversationKind.HumanAgent)]
    [InlineData("AgentAgent", ConversationKind.AgentAgent)]
    [InlineData("agentsubagent", ConversationKind.AgentSubAgent)]
    public void ParseKind_ParsesEveryWireValue_CaseInsensitively(string wire, ConversationKind expected) =>
        Assert.Equal(expected, ConversationOrigin.ParseKind(wire));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData("9")]
    public void ParseKind_FallsBackToHumanAgent_ForUnknownOrAbsentValues(string? wire) =>
        Assert.Equal(ConversationKind.HumanAgent, ConversationOrigin.ParseKind(wire));

    // ── Projection truth table: every (Kind, Source) combination ─────────────

    public static TheoryData<ConversationKind, ConversationSource, bool, ConversationListGroup, string?> TruthTable =>
        new()
        {
            // Kind, Source, expected IsReadOnly (under a normal user selection), Group, Badge
            { ConversationKind.HumanAgent,    ConversationSource.Channel, false, ConversationListGroup.Normal,         null },
            { ConversationKind.HumanAgent,    ConversationSource.Cron,    true,  ConversationListGroup.Scheduled,      "Cron" },
            { ConversationKind.HumanAgent,    ConversationSource.Webhook, true,  ConversationListGroup.Automated,      "Webhook" },
            { ConversationKind.HumanAgent,    ConversationSource.Agent,   false, ConversationListGroup.Normal,         null },

            { ConversationKind.AgentAgent,    ConversationSource.Channel, true,  ConversationListGroup.AgentInitiated, "Read-only" },
            { ConversationKind.AgentAgent,    ConversationSource.Cron,    true,  ConversationListGroup.AgentInitiated, "Read-only" },
            { ConversationKind.AgentAgent,    ConversationSource.Webhook, true,  ConversationListGroup.AgentInitiated, "Read-only" },
            { ConversationKind.AgentAgent,    ConversationSource.Agent,   true,  ConversationListGroup.AgentInitiated, "Read-only" },

            { ConversationKind.AgentSubAgent, ConversationSource.Channel, true,  ConversationListGroup.AgentInitiated, "Read-only" },
            { ConversationKind.AgentSubAgent, ConversationSource.Cron,    true,  ConversationListGroup.AgentInitiated, "Read-only" },
            { ConversationKind.AgentSubAgent, ConversationSource.Webhook, true,  ConversationListGroup.AgentInitiated, "Read-only" },
            { ConversationKind.AgentSubAgent, ConversationSource.Agent,   true,  ConversationListGroup.AgentInitiated, "Read-only" }
        };

    [Theory]
    [MemberData(nameof(TruthTable))]
    public void Projection_IsDeterministicAcrossEveryKindSourceCombination(
        ConversationKind kind,
        ConversationSource source,
        bool expectedReadOnly,
        ConversationListGroup expectedGroup,
        string? expectedBadge)
    {
        var conversation = new ConversationState { ConversationId = "c-1", Kind = kind, Source = source };

        var projection = conversation.Project(SelectionSource.UserClick);

        Assert.Equal(expectedReadOnly, projection.IsReadOnly);
        Assert.Equal(!expectedReadOnly, projection.ShowComposer);
        Assert.Equal(expectedGroup, projection.Group);
        Assert.Equal(expectedBadge, projection.Badge);
    }

    [Fact]
    public void TruthTable_CoversEveryKindSourceCombination()
    {
        var kinds = Enum.GetValues<ConversationKind>().Length;
        var sources = Enum.GetValues<ConversationSource>().Length;

        Assert.Equal(kinds * sources, TruthTable.Count);
    }

    [Theory]
    [InlineData(SelectionSource.UserClick)]
    [InlineData(SelectionSource.RouteNavigation)]
    [InlineData(SelectionSource.Bootstrap)]
    public void HumanChannelConversation_IsWritable_UnderEveryNonObserverSelectionSource(SelectionSource selection)
    {
        var conversation = new ConversationState { ConversationId = "c-1" };

        var projection = conversation.Project(selection);

        Assert.False(projection.IsReadOnly);
        Assert.True(projection.ShowComposer);
        Assert.Equal(ConversationListGroup.Normal, projection.Group);
        Assert.Null(projection.Badge);
    }

    [Fact]
    public void SubAgentViewSelection_MakesEvenAHumanChannelConversationReadOnly()
    {
        var conversation = new ConversationState { ConversationId = "c-1" };

        var projection = conversation.Project(SelectionSource.SubAgentView);

        Assert.True(projection.IsReadOnly);
        Assert.False(projection.ShowComposer);

        // The conversation itself is still attended — read-only here is purely a view concern.
        Assert.False(projection.IsUnattended);
    }

    // ── #2526: an agent-minted user-facing conversation is NOT an observer row ───────

    /// <summary>
    /// The <c>conversation_new</c> tool mints <c>(Kind=HumanAgent, Source=Agent)</c>. The user is a
    /// participant, so the thread must be writable. Before #2526 the unconditional
    /// <c>Source is Agent</c> disjunct in <c>IsUnattended</c> made this cell read-only.
    /// </summary>
    [Theory]
    [InlineData(SelectionSource.UserClick)]
    [InlineData(SelectionSource.RouteNavigation)]
    [InlineData(SelectionSource.Bootstrap)]
    public void AgentMintedUserFacingConversation_IsWritableAndNormallyGrouped(SelectionSource selection)
    {
        var conversation = new ConversationState
        {
            ConversationId = "c-1",
            Kind = ConversationKind.HumanAgent,
            Source = ConversationSource.Agent
        };

        var projection = conversation.Project(selection);

        Assert.False(projection.IsUnattended);
        Assert.False(projection.IsReadOnly);
        Assert.True(projection.ShowComposer);
        Assert.Equal(ConversationListGroup.Normal, projection.Group);
        Assert.Null(projection.Badge);
    }

    /// <summary>
    /// #2243/#2248/#2299 guard: loosening the <c>Source</c> axis must not make any genuine
    /// agent-to-agent or sub-agent thread writable, whatever triggered it.
    /// </summary>
    [Theory]
    [InlineData(ConversationKind.AgentAgent, ConversationSource.Channel)]
    [InlineData(ConversationKind.AgentAgent, ConversationSource.Cron)]
    [InlineData(ConversationKind.AgentAgent, ConversationSource.Webhook)]
    [InlineData(ConversationKind.AgentAgent, ConversationSource.Agent)]
    [InlineData(ConversationKind.AgentSubAgent, ConversationSource.Channel)]
    [InlineData(ConversationKind.AgentSubAgent, ConversationSource.Cron)]
    [InlineData(ConversationKind.AgentSubAgent, ConversationSource.Webhook)]
    [InlineData(ConversationKind.AgentSubAgent, ConversationSource.Agent)]
    public void AgentPairedConversations_StayReadOnlyAndAgentInitiated(
        ConversationKind kind,
        ConversationSource source)
    {
        var conversation = new ConversationState { ConversationId = "c-1", Kind = kind, Source = source };

        var projection = conversation.Project(SelectionSource.UserClick);

        Assert.True(projection.IsUnattended);
        Assert.True(projection.IsReadOnly);
        Assert.False(projection.ShowComposer);
        Assert.Equal(ConversationListGroup.AgentInitiated, projection.Group);
        Assert.Equal("Read-only", projection.Badge);
    }

    /// <summary>Cron and webhook runs stay unattended regardless of pairing (#2526 must not widen).</summary>
    [Theory]
    [InlineData(ConversationSource.Cron, ConversationListGroup.Scheduled, "Cron")]
    [InlineData(ConversationSource.Webhook, ConversationListGroup.Automated, "Webhook")]
    public void CronAndWebhookConversations_StayUnattendedAndReadOnly(
        ConversationSource source,
        ConversationListGroup expectedGroup,
        string expectedBadge)
    {
        var conversation = new ConversationState
        {
            ConversationId = "c-1",
            Kind = ConversationKind.HumanAgent,
            Source = source
        };

        var projection = conversation.Project(SelectionSource.UserClick);

        Assert.True(projection.IsUnattended);
        Assert.True(projection.IsReadOnly);
        Assert.False(projection.ShowComposer);
        Assert.Equal(expectedGroup, projection.Group);
        Assert.Equal(expectedBadge, projection.Badge);
    }

    /// <summary>
    /// The observer-view selection still forces read-only even for an agent-minted user
    /// conversation, so #2299's "view sub-agent" behaviour is unchanged.
    /// </summary>
    [Fact]
    public void AgentMintedUserFacingConversation_IsStillReadOnly_UnderSubAgentViewSelection()
    {
        var conversation = new ConversationState
        {
            ConversationId = "c-1",
            Kind = ConversationKind.HumanAgent,
            Source = ConversationSource.Agent
        };

        var projection = conversation.Project(SelectionSource.SubAgentView);

        Assert.True(projection.IsReadOnly);
        Assert.False(projection.ShowComposer);
        Assert.False(projection.IsUnattended);
    }

    // ── Seeding from the server payload ──────────────────────────────────────

    [Fact]
    public void SeedConversations_SeedsSourceAndKindFromTheServerPayload()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);

        store.SeedConversations("a-1",
        [
            Dto("c-user"),
            Dto("c-cron", source: "Cron"),
            Dto("c-webhook", source: "Webhook")
        ]);

        var conversations = store.GetAgent("a-1")!.Conversations;
        Assert.Equal(ConversationSource.Channel, conversations["c-user"].Source);
        Assert.Equal(ConversationKind.HumanAgent, conversations["c-user"].Kind);
        Assert.Equal(ConversationSource.Cron, conversations["c-cron"].Source);
        Assert.Equal(ConversationSource.Webhook, conversations["c-webhook"].Source);
    }

    [Fact]
    public void SeedConversations_DefaultsSourceToChannel_ForALegacyServerPayload()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);

        // A pre-#2300 server omits `source`; the DTO default is the empty-ish back-compat value.
        store.SeedConversations("a-1",
        [
            new ConversationSummaryDto("c-1", "a-1", "Legacy", false, "Active", null, 0,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        ]);

        var conversation = store.GetAgent("a-1")!.Conversations["c-1"];
        Assert.Equal(ConversationSource.Channel, conversation.Source);
        Assert.False(conversation.Project(SelectionSource.UserClick).IsReadOnly);
    }

    // ── #2248-class regression guard, applied to conversations ───────────────

    [Fact]
    public void InboundSubAgentEvent_DoesNotChangeAUserConversationsSourceOrWritability()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.SeedConversations("a-1", [Dto("c-user")]);
        store.SelectView("a-1", "c-user", SelectionSource.UserClick);

        var userConversation = store.GetAgent("a-1")!.Conversations["c-user"];
        Assert.Equal(ConversationSource.Channel, userConversation.Source);
        Assert.True(userConversation.Project(store.ActiveSelectionSource).ShowComposer);

        // Act: an inbound sub-agent event registers a sub-agent session against the SAME agent,
        // and an inbound cron event tries to move the view. Neither is a user interaction.
        store.MarkSubAgent("a-1-sub");
        store.RegisterSession("a-1", "sess-sub", sessionType: "agent-subagent", conversationId: "c-user");
        store.SelectView("a-1-sub", string.Empty, SelectionSource.Bootstrap);

        // Assert: the user's conversation origin is untouched and it stays writable + listed.
        var after = store.GetAgent("a-1")!.Conversations["c-user"];
        Assert.Same(userConversation, after);
        Assert.Equal(ConversationSource.Channel, after.Source);
        Assert.Equal(ConversationKind.HumanAgent, after.Kind);

        var projection = after.Project(store.ActiveSelectionSource);
        Assert.False(projection.IsReadOnly);
        Assert.True(projection.ShowComposer);
        Assert.Equal(ConversationListGroup.Normal, projection.Group);
        Assert.Null(projection.Badge);
    }

    [Fact]
    public void ConversationRefresh_CannotRewriteAnAlreadySeededSource()
    {
        var store = new ClientStateStore();
        store.SeedAgents([new AgentSummary("a-1", "Alpha")]);
        store.SeedConversations("a-1", [Dto("c-user")]);

        // A later refresh claiming a different origin must not be able to rewrite it: Source is
        // write-once, so the conversation the user is typing into can never become read-only.
        // (The refresh keeps Kind=HumanAgent because SeedConversations filters non-HumanAgent rows
        // out of the portal list entirely - Source is the axis that could otherwise be rewritten.)
        store.SeedConversations("a-1", [Dto("c-user", source: "Cron")]);

        var conversation = store.GetAgent("a-1")!.Conversations["c-user"];
        Assert.Equal(ConversationSource.Channel, conversation.Source);
        Assert.Equal(ConversationKind.HumanAgent, conversation.Kind);
        Assert.True(conversation.Project(SelectionSource.UserClick).ShowComposer);
    }

    [Fact]
    public void SourceAndKind_AreInitOnly_AndHaveNoPublicSetter()
    {
        var sourceSetter = typeof(ConversationState).GetProperty(nameof(ConversationState.Source))!.SetMethod!;
        var kindSetter = typeof(ConversationState).GetProperty(nameof(ConversationState.Kind))!.SetMethod!;

        // An init-only setter carries the IsExternalInit modreq; a plain `set` does not. This is the
        // structural guarantee that no inbound event handler can assign either property (#2304).
        Assert.Contains(
            typeof(System.Runtime.CompilerServices.IsExternalInit),
            sourceSetter.ReturnParameter.GetRequiredCustomModifiers());
        Assert.Contains(
            typeof(System.Runtime.CompilerServices.IsExternalInit),
            kindSetter.ReturnParameter.GetRequiredCustomModifiers());
    }
}
