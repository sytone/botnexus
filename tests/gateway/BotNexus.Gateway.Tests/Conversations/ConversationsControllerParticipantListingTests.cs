using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Conversations;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>
/// Phase 9 / P9-G (#661) regression suite. Covers the contract flip where
/// <c>GET /api/conversations?agentId={id}</c> moved from owner-only filtering
/// (<c>IConversationStore.GetSummariesAsync(agentId)</c> — now deleted) to
/// "relevant-to-citizen" semantics via
/// <see cref="IConversationStore.ListForCitizenAsync"/>:
/// <list type="bullet">
///   <item>Owner-only (initiator = agent) is still returned (W-1 owner-side baseline).</item>
///   <item>Participant-only (agent is in <c>Conversation.Participants</c> but not the owner) is now returned — the responder-side gap the user flagged.</item>
///   <item>Owner+Participant returns the conversation exactly once (DISTINCT semantics from the union).</item>
///   <item>Archived conversations are excluded from the agent-filtered branch — status filtering lives in the controller projection.</item>
///   <item><see cref="ConversationKind.AgentAgent"/> / <see cref="ConversationKind.AgentSubAgent"/> kinds are present in the API result set even though the portal sidebar deliberately hides them (UX is filtered downstream in the BlazorClient).</item>
/// </list>
/// </summary>
public sealed class ConversationsControllerParticipantListingTests
{
    private static readonly AgentId OwnerAgent = AgentId.From("agent-owner");
    private static readonly AgentId ParticipantAgent = AgentId.From("agent-participant");

    [Fact]
    public async Task List_AgentFiltered_ReturnsOwnerInitiatedConversation()
    {
        var conversations = new InMemoryConversationStore();
        var owned = await conversations.CreateAsync(NewConversation(OwnerAgent, "owner-only"));

        var controller = new ConversationsController(conversations, new InMemorySessionStore());

        var summaries = await InvokeList(controller, OwnerAgent.Value);

        summaries.ShouldContain(s => s.ConversationId == owned.ConversationId.Value);
    }

    [Fact]
    public async Task List_AgentFiltered_ReturnsConversationWhereAgentIsParticipantNotOwner()
    {
        // The whole point of P9-G: a responder-side agent must see exchanges it didn't
        // initiate. Pre-flip, the owner-only filter (c.agent_id = $agentId) hid these.
        var conversations = new InMemoryConversationStore();
        var participantOnly = await conversations.CreateAsync(
            NewConversation(OwnerAgent, "participant-side", kind: ConversationKind.AgentAgent));

        await conversations.AddParticipantsAsync(
            participantOnly.ConversationId,
            [new SessionParticipant { CitizenId = CitizenId.Of(ParticipantAgent), Role = "peer" }]);

        var controller = new ConversationsController(conversations, new InMemorySessionStore());

        var summaries = await InvokeList(controller, ParticipantAgent.Value);

        summaries.ShouldContain(
            s => s.ConversationId == participantOnly.ConversationId.Value,
            "participant-side agent must see a conversation it joined but did not initiate (W-1)");
    }

    [Fact]
    public async Task List_AgentFiltered_OwnerAndParticipant_DoesNotDuplicateConversation()
    {
        // The union of (owner-match) ∪ (participant-match) must be DISTINCT by
        // ConversationId — otherwise the portal renders a duplicate row in the sidebar.
        var conversations = new InMemoryConversationStore();
        var owned = await conversations.CreateAsync(NewConversation(OwnerAgent, "both"));

        // Add the owner as a participant too (the routing layer is allowed to do this;
        // the AddParticipantsAsync contract is INSERT OR IGNORE).
        await conversations.AddParticipantsAsync(
            owned.ConversationId,
            [new SessionParticipant { CitizenId = CitizenId.Of(OwnerAgent), Role = "initiator" }]);

        var controller = new ConversationsController(conversations, new InMemorySessionStore());

        var summaries = await InvokeList(controller, OwnerAgent.Value);

        summaries.Count(s => s.ConversationId == owned.ConversationId.Value).ShouldBe(1);
    }

    [Fact]
    public async Task List_AgentFiltered_ExcludesArchivedParticipantSideConversation()
    {
        // ListForCitizenAsync returns conversations in any status; the controller
        // projection MUST drop archived rows so the portal sidebar doesn't surface them.
        var conversations = new InMemoryConversationStore();
        var archived = await conversations.CreateAsync(
            NewConversation(OwnerAgent, "archived-participant", kind: ConversationKind.AgentAgent));

        await conversations.AddParticipantsAsync(
            archived.ConversationId,
            [new SessionParticipant { CitizenId = CitizenId.Of(ParticipantAgent), Role = "peer" }]);

        await conversations.ArchiveAsync(archived.ConversationId);

        var controller = new ConversationsController(conversations, new InMemorySessionStore());

        var summaries = await InvokeList(controller, ParticipantAgent.Value);

        summaries.ShouldNotContain(s => s.ConversationId == archived.ConversationId.Value);
    }

    [Fact]
    public async Task List_AgentFiltered_IncludesAgentAgentAndAgentSubAgentKinds()
    {
        // Platform/API surface must expose all kinds — UX filtering (HumanAgent-only
        // sidebar in ClientStateStore.SeedConversations) is a deliberate downstream
        // choice. Future "exchanges view" should be able to opt in to these without
        // a further API change.
        var conversations = new InMemoryConversationStore();
        var aa = await conversations.CreateAsync(
            NewConversation(OwnerAgent, "agent-agent", kind: ConversationKind.AgentAgent));
        var asa = await conversations.CreateAsync(
            NewConversation(OwnerAgent, "agent-subagent", kind: ConversationKind.AgentSubAgent));

        await conversations.AddParticipantsAsync(
            aa.ConversationId,
            [new SessionParticipant { CitizenId = CitizenId.Of(ParticipantAgent), Role = "peer" }]);
        await conversations.AddParticipantsAsync(
            asa.ConversationId,
            [new SessionParticipant { CitizenId = CitizenId.Of(ParticipantAgent), Role = "child" }]);

        var controller = new ConversationsController(conversations, new InMemorySessionStore());

        var summaries = await InvokeList(controller, ParticipantAgent.Value);

        summaries.ShouldContain(s =>
            s.ConversationId == aa.ConversationId.Value &&
            s.Kind == ConversationKind.AgentAgent.ToString());
        summaries.ShouldContain(s =>
            s.ConversationId == asa.ConversationId.Value &&
            s.Kind == ConversationKind.AgentSubAgent.ToString());
    }

    [Fact]
    public async Task List_AgentFiltered_OrdersByUpdatedAtDescendingWithDeterministicTieBreaker()
    {
        // Most-recently-updated first. When two conversations share an UpdatedAt
        // (clock granularity or backfilled rows), tie-break on ConversationId Ordinal
        // so portal rendering doesn't flicker between requests.
        var conversations = new InMemoryConversationStore();
        var t0 = DateTimeOffset.UtcNow;

        var older = await conversations.CreateAsync(NewConversation(OwnerAgent, "older", updatedAt: t0.AddMinutes(-10)));
        var newer = await conversations.CreateAsync(NewConversation(OwnerAgent, "newer", updatedAt: t0));
        // Two rows sharing the same UpdatedAt to verify the deterministic tie-breaker.
        var tieA = await conversations.CreateAsync(
            new Conversation
            {
                ConversationId = ConversationId.From("tie-a"),
                AgentId = OwnerAgent,
                Title = "tie-a",
                Status = ConversationStatus.Active,
                UpdatedAt = t0.AddMinutes(-5),
                CreatedAt = t0.AddMinutes(-5)
            });
        var tieB = await conversations.CreateAsync(
            new Conversation
            {
                ConversationId = ConversationId.From("tie-b"),
                AgentId = OwnerAgent,
                Title = "tie-b",
                Status = ConversationStatus.Active,
                UpdatedAt = t0.AddMinutes(-5),
                CreatedAt = t0.AddMinutes(-5)
            });

        var controller = new ConversationsController(conversations, new InMemorySessionStore());

        var summaries = await InvokeList(controller, OwnerAgent.Value);
        var ids = summaries.Select(s => s.ConversationId).ToList();

        ids.IndexOf(newer.ConversationId.Value).ShouldBeLessThan(ids.IndexOf(tieA.ConversationId.Value));
        ids.IndexOf(tieA.ConversationId.Value).ShouldBeLessThan(ids.IndexOf(tieB.ConversationId.Value));
        ids.IndexOf(tieB.ConversationId.Value).ShouldBeLessThan(ids.IndexOf(older.ConversationId.Value));
    }

    private static async Task<IReadOnlyList<ConversationSummary>> InvokeList(
        ConversationsController controller,
        string? agentId)
    {
        var result = await controller.List(agentId, CancellationToken.None);
        var ok = result.ShouldBeOfType<OkObjectResult>();
        return (IReadOnlyList<ConversationSummary>)ok.Value!;
    }

    private static Conversation NewConversation(
        AgentId owner,
        string title,
        ConversationKind kind = ConversationKind.HumanAgent,
        DateTimeOffset? updatedAt = null)
    {
        var ts = updatedAt ?? DateTimeOffset.UtcNow;
        return new Conversation
        {
            ConversationId = ConversationId.Create(),
            AgentId = owner,
            Title = title,
            Kind = kind,
            Status = ConversationStatus.Active,
            CreatedAt = ts,
            UpdatedAt = ts
        };
    }
}
