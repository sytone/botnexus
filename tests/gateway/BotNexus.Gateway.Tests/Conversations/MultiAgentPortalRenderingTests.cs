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
/// Tests for multi-agent portal rendering support (#415):
/// - Participant roster exposed in conversation summary
/// - Agent attribution on conversation history entries
/// </summary>
public sealed class MultiAgentPortalRenderingTests
{
    private static readonly AgentId HostAgent = AgentId.From("host-agent");
    private static readonly AgentId PeerAgent = AgentId.From("peer-agent");

    // ── Participant roster in conversation summaries ──────────────────────

    [Fact]
    public async Task List_Conversation_IncludesParticipantRoster()
    {
        var conversations = new InMemoryConversationStore();
        var conversation = await conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From("c_multiagent_1"),
            AgentId = HostAgent,
            Title = "Multi-agent test",
            Kind = ConversationKind.AgentAgent
        });

        await conversations.AddParticipantsAsync(
            conversation.ConversationId,
            [
                new SessionParticipant { CitizenId = CitizenId.Of(HostAgent), Role = "initiator" },
                new SessionParticipant { CitizenId = CitizenId.Of(PeerAgent), Role = "peer" }
            ]);

        var controller = new ConversationsController(conversations, new InMemorySessionStore());
        var result = await controller.List(HostAgent.Value, CancellationToken.None);

        var summaries = ((result as OkObjectResult)?.Value as IEnumerable<ConversationSummary>)?.ToList();
        summaries.ShouldNotBeNull();
        summaries!.Count.ShouldBe(1);
        summaries[0].Participants.ShouldNotBeNull();
        summaries[0].Participants!.Count.ShouldBe(2);
        summaries[0].Participants!.ShouldContain(p => p.Id == HostAgent.Value && p.Role == "initiator");
        summaries[0].Participants!.ShouldContain(p => p.Id == PeerAgent.Value && p.Role == "peer");
    }

    [Fact]
    public async Task List_HumanAgentConversation_ParticipantsIncludesHumanUser()
    {
        var conversations = new InMemoryConversationStore();
        var conversation = await conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From("c_human_agent_1"),
            AgentId = HostAgent,
            Title = "Human-agent test",
            Kind = ConversationKind.HumanAgent
        });

        await conversations.AddParticipantsAsync(
            conversation.ConversationId,
            [
                new SessionParticipant { CitizenId = CitizenId.Of(UserId.From("user-123")), Role = "initiator" },
                new SessionParticipant { CitizenId = CitizenId.Of(HostAgent), Role = "responder" }
            ]);

        var controller = new ConversationsController(conversations, new InMemorySessionStore());
        var result = await controller.List(HostAgent.Value, CancellationToken.None);

        var summaries = ((result as OkObjectResult)?.Value as IEnumerable<ConversationSummary>)?.ToList();
        summaries.ShouldNotBeNull();
        summaries![0].Participants.ShouldNotBeNull();
        summaries[0].Participants!.Count.ShouldBe(2);
        summaries[0].Participants!.ShouldContain(p => p.Kind == "User" && p.Id == "user-123");
    }

    [Fact]
    public async Task List_ConversationWithNoParticipants_ReturnsEmptyList()
    {
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = ConversationId.From("c_empty_participants"),
            AgentId = HostAgent,
            Title = "No participants",
            Kind = ConversationKind.HumanAgent
        });

        var controller = new ConversationsController(conversations, new InMemorySessionStore());
        var result = await controller.List(HostAgent.Value, CancellationToken.None);

        var summaries = ((result as OkObjectResult)?.Value as IEnumerable<ConversationSummary>)?.ToList();
        summaries.ShouldNotBeNull();
        summaries![0].Participants.ShouldNotBeNull();
        summaries[0].Participants!.Count.ShouldBe(0);
    }

    // ── Agent attribution on history entries ──────────────────────────────

    [Fact]
    public async Task GetHistory_MultiAgentConversation_EntriesIncludeAgentId()
    {
        var conversationId = ConversationId.From("c_multiagent_history");
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();

        // Host agent session
        var hostSession = await sessions.GetOrCreateAsync(SessionId.From("s-host-1"), HostAgent);
        hostSession.Session.ConversationId = conversationId;
        hostSession.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "Hello from host",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-2)
        });
        await sessions.SaveAsync(hostSession);

        // Peer agent session
        var peerSession = await sessions.GetOrCreateAsync(SessionId.From("s-peer-1"), PeerAgent);
        peerSession.Session.ConversationId = conversationId;
        peerSession.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "Hello from peer",
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
        });
        await sessions.SaveAsync(peerSession);

        // Conversation with ActiveSessionId set for fallback path
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = HostAgent,
            Title = "Multi-agent history",
            Kind = ConversationKind.AgentAgent,
            ActiveSessionId = SessionId.From("s-host-1")
        });

        var controller = new ConversationsController(conversations, sessions);
        var actionResult = await controller.GetHistory(conversationId.Value, limit: 50, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();
        response!.Entries.Count.ShouldBeGreaterThanOrEqualTo(2);

        var hostEntry = response.Entries.First(e => e.Content == "Hello from host");
        hostEntry.AgentId.ShouldBe(HostAgent.Value);

        var peerEntry = response.Entries.First(e => e.Content == "Hello from peer");
        peerEntry.AgentId.ShouldBe(PeerAgent.Value);
    }

    [Fact]
    public async Task GetHistory_SingleAgentConversation_EntriesStillIncludeAgentId()
    {
        var conversationId = ConversationId.From("c_single_agent_history");
        var sessions = new InMemorySessionStore();
        var conversations = new InMemoryConversationStore();
        await conversations.CreateAsync(new Conversation
        {
            ConversationId = conversationId,
            AgentId = HostAgent,
            Title = "Single agent",
            Kind = ConversationKind.HumanAgent
        });

        var session = await sessions.GetOrCreateAsync(SessionId.From("s-single-1"), HostAgent);
        session.Session.ConversationId = conversationId;
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.User,
            Content = "User message"
        });
        session.AddEntry(new SessionEntry
        {
            Role = MessageRole.Assistant,
            Content = "Agent reply"
        });
        await sessions.SaveAsync(session);

        var controller = new ConversationsController(conversations, sessions);
        var actionResult = await controller.GetHistory(conversationId.Value, limit: 50, offset: 0, CancellationToken.None);

        var response = (actionResult as OkObjectResult)?.Value as ConversationHistoryResponse;
        response.ShouldNotBeNull();

        // All entries in a single-agent conversation carry the same AgentId
        foreach (var entry in response!.Entries.Where(e => e.Kind == "message"))
        {
            entry.AgentId.ShouldBe(HostAgent.Value);
        }
    }
}
