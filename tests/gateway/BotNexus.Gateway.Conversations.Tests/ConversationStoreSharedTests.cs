using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Conversations;

namespace BotNexus.Gateway.Conversations.Tests;

/// <summary>
/// Direct unit coverage for <see cref="ConversationStoreShared"/> — the world-id stamping/back-fill
/// and citizen-matching logic hoisted out of the three conversation stores (#1383, Finding 3). Before
/// the extraction these routines were copy-pasted byte-for-byte across the stores with no test guarding
/// the shared logic itself; the store contract tests exercised it only indirectly. These tests pin the
/// behaviour at the single shared source so any future change is caught here.
/// </summary>
public sealed class ConversationStoreSharedTests
{
    private static Conversation NewConversation()
        => new()
        {
            ConversationId = ConversationId.Create(),
            AgentId = AgentId.From("agent-a"),
            Title = "t",
            Status = ConversationStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    // ── StampWorldId ─────────────────────────────────────────────────────

    [Fact]
    public void StampWorldId_FillsEmpty_FromWorldContext()
    {
        var conv = NewConversation();
        conv.WorldId.ShouldBe(string.Empty);

        ConversationStoreShared.StampWorldId(conv, new FakeWorldContext("world-x"));

        conv.WorldId.ShouldBe("world-x");
    }

    [Fact]
    public void StampWorldId_PreservesExplicitNonEmptyValue()
    {
        var conv = NewConversation();
        conv.WorldId = "world-y"; // e.g. a cross-world relay receiver holding the source world id.

        ConversationStoreShared.StampWorldId(conv, new FakeWorldContext("world-x"));

        conv.WorldId.ShouldBe("world-y", customMessage: "Stamp must only fill an EMPTY WorldId.");
    }

    [Fact]
    public void StampWorldId_NoOp_WhenWorldContextNull()
    {
        var conv = NewConversation();

        ConversationStoreShared.StampWorldId(conv, worldContext: null);

        conv.WorldId.ShouldBe(string.Empty, customMessage: "No world context wired (parameterless-ctor case) is a no-op.");
    }

    // ── BackfillWorldId ──────────────────────────────────────────────────

    [Fact]
    public void BackfillWorldId_ProjectsEmpty_ToCurrentWorld()
    {
        var conv = NewConversation();

        var result = ConversationStoreShared.BackfillWorldId(conv, new FakeWorldContext("world-x"));

        result.ShouldBeSameAs(conv);
        result!.WorldId.ShouldBe("world-x");
    }

    [Fact]
    public void BackfillWorldId_PreservesExplicitValue()
    {
        var conv = NewConversation();
        conv.WorldId = "world-y";

        var result = ConversationStoreShared.BackfillWorldId(conv, new FakeWorldContext("world-x"));

        result!.WorldId.ShouldBe("world-y");
    }

    [Fact]
    public void BackfillWorldId_ReturnsNull_ForNullInput()
    {
        var result = ConversationStoreShared.BackfillWorldId(conversation: null, new FakeWorldContext("world-x"));

        result.ShouldBeNull();
    }

    // ── MatchesCitizen ───────────────────────────────────────────────────

    [Fact]
    public void MatchesCitizen_True_WhenCitizenIsInitiator()
    {
        var user = CitizenId.Of(UserId.From("user-1"));
        var conv = NewConversation();
        conv.Initiator = user;

        ConversationStoreShared.MatchesCitizen(conv, user).ShouldBeTrue();
    }

    [Fact]
    public void MatchesCitizen_True_WhenAgentCitizenOwnsConversation()
    {
        var conv = NewConversation(); // AgentId = agent-a
        var owner = CitizenId.Of(AgentId.From("agent-a"));

        ConversationStoreShared.MatchesCitizen(conv, owner).ShouldBeTrue();
    }

    [Fact]
    public void MatchesCitizen_True_WhenCitizenIsParticipant()
    {
        var participant = CitizenId.Of(AgentId.From("agent-peer"));
        var conv = NewConversation();
        conv.Participants.Add(new SessionParticipant { CitizenId = participant });

        ConversationStoreShared.MatchesCitizen(conv, participant).ShouldBeTrue();
    }

    [Fact]
    public void MatchesCitizen_False_WhenUnrelated()
    {
        var conv = NewConversation(); // owner agent-a, no initiator, no participants
        var stranger = CitizenId.Of(AgentId.From("agent-z"));

        ConversationStoreShared.MatchesCitizen(conv, stranger).ShouldBeFalse();
    }

    [Fact]
    public void MatchesCitizen_OwnerMatch_OnlyForAgentSpeciesNotUser()
    {
        // A user citizen whose value happens to equal the owning agent id must NOT owner-match —
        // owner-match is agent-species only. (Guards the CitizenKind.Agent gate in the shared logic.)
        var conv = NewConversation(); // owner agent-a
        var userNamedLikeAgent = CitizenId.Of(UserId.From("agent-a"));

        ConversationStoreShared.MatchesCitizen(conv, userNamedLikeAgent).ShouldBeFalse();
    }
}
