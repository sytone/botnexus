using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Agents;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using GatewaySessionStatus = BotNexus.Gateway.Abstractions.Models.SessionStatus;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Probe round 3 — surfaces 3 &amp; 5 edge-case coverage.
/// </summary>
public sealed class ProbeRound3GatewayTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static AgentId Agent(string id = "agent-a") => AgentId.From(id);

    private static Conversation MakeConversation(string title = "Test") => new()
    {
        ConversationId = ConversationId.Create(),
        AgentId = Agent(),
        Title = title
    };

    private static ConversationsController CreateConvController(IConversationStore store) =>
        new(store, new InMemorySessionStore());

    // ══════════════════════════════════════════════════════════════════════
    // Surface 3 — Conversation rename / archive / delete
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Patch_WithNewTitle_PersistsTitleAndReturnsOk()
    {
        var store = new InMemoryConversationStore();
        var conv = await store.CreateAsync(MakeConversation("original"));
        var controller = CreateConvController(store);

        var result = await controller.Patch(conv.ConversationId.Value,
            new PatchConversationRequest("updated-title"), CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        var loaded = await store.GetAsync(conv.ConversationId);
        loaded!.Title.ShouldBe("updated-title");
    }

    [Fact]
    public async Task Patch_WithEmptyTitle_ReturnsBadRequest()
    {
        var store = new InMemoryConversationStore();
        var conv = await store.CreateAsync(MakeConversation("original"));
        var controller = CreateConvController(store);

        var result = await controller.Patch(conv.ConversationId.Value,
            new PatchConversationRequest(""), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Patch_WithWhitespaceTitle_ReturnsBadRequest()
    {
        var store = new InMemoryConversationStore();
        var conv = await store.CreateAsync(MakeConversation("original"));
        var controller = CreateConvController(store);

        var result = await controller.Patch(conv.ConversationId.Value,
            new PatchConversationRequest("   "), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Patch_WithTitleOver500Chars_ReturnsBadRequest()
    {
        var store = new InMemoryConversationStore();
        var conv = await store.CreateAsync(MakeConversation("original"));
        var controller = CreateConvController(store);

        var longTitle = new string('x', 501);
        var result = await controller.Patch(conv.ConversationId.Value,
            new PatchConversationRequest(longTitle), CancellationToken.None);

        result.ShouldBeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Patch_WithMissingConversation_ReturnsNotFound()
    {
        var controller = CreateConvController(new InMemoryConversationStore());

        var result = await controller.Patch("non-existent-id",
            new PatchConversationRequest("new title"), CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task GetSummaries_ExcludesArchivedConversations()
    {
        var store = new InMemoryConversationStore();
        var active = await store.CreateAsync(MakeConversation("active"));
        var archived = await store.CreateAsync(MakeConversation("archived"));
        await store.ArchiveAsync(archived.ConversationId);

        var summaries = await store.GetSummariesAsync();

        summaries.ShouldAllBe(s => s.Status == "Active");
        summaries.ShouldContain(s => s.ConversationId == active.ConversationId.Value);
        summaries.ShouldNotContain(s => s.ConversationId == archived.ConversationId.Value);
    }

    [Fact]
    public async Task GetSummaries_ByAgent_ExcludesArchivedConversations()
    {
        var store = new InMemoryConversationStore();
        var conv = await store.CreateAsync(MakeConversation("active"));
        var archConv = await store.CreateAsync(MakeConversation("archived"));
        await store.ArchiveAsync(archConv.ConversationId);

        var summaries = await store.GetSummariesAsync(Agent());

        summaries.ShouldContain(s => s.ConversationId == conv.ConversationId.Value);
        summaries.ShouldNotContain(s => s.ConversationId == archConv.ConversationId.Value);
    }

    [Fact]
    public async Task ConversationsController_List_ExcludesArchivedConversations()
    {
        var store = new InMemoryConversationStore();
        var active = await store.CreateAsync(MakeConversation("visible"));
        var archived = await store.CreateAsync(MakeConversation("hidden"));
        await store.ArchiveAsync(archived.ConversationId);

        var controller = CreateConvController(store);
        // Pass the agent ID as a string to avoid implicit AgentId conversion ambiguity
        var result = await controller.List("agent-a", CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var list = (ok.Value as IReadOnlyList<ConversationSummary>)!;
        list.ShouldContain(s => s.ConversationId == active.ConversationId.Value);
        list.ShouldNotContain(s => s.ConversationId == archived.ConversationId.Value);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Surface 5 — Session lifecycle edge cases
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Seal_WithExpiredSubAgentSession_Returns200AndStatusIsSealed()
    {
        // Covered by existing tests — just verify Seal endpoint path is tested
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("parent::subagent::child1", "agent-a");
        session.Status = GatewaySessionStatus.Expired;
        await store.SaveAsync(session);

        var controller = new SessionsController(store);
        var result = await controller.Seal("parent::subagent::child1", CancellationToken.None);

        result.ShouldBeOfType<OkObjectResult>();
        var loaded = await store.GetAsync("parent::subagent::child1");
        loaded!.Status.ShouldBe(GatewaySessionStatus.Sealed);
    }

    [Fact]
    public async Task Suspend_WithActiveSession_TransitionsToSuspended()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Status = GatewaySessionStatus.Active;
        await store.SaveAsync(session);

        var controller = new SessionsController(store);
        var result = await controller.Suspend("s1", CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        var loaded = await store.GetAsync("s1");
        loaded!.Status.ShouldBe(GatewaySessionStatus.Suspended);
    }

    [Fact]
    public async Task Resume_WithSuspendedSession_TransitionsToActive()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("s1", "agent-a");
        session.Status = GatewaySessionStatus.Suspended;
        await store.SaveAsync(session);

        var controller = new SessionsController(store);
        var result = await controller.Resume("s1", CancellationToken.None);

        result.Result.ShouldBeOfType<OkObjectResult>();
        var loaded = await store.GetAsync("s1");
        loaded!.Status.ShouldBe(GatewaySessionStatus.Active);
    }

    [Fact]
    public async Task SendMessageToSealedSession_ChatController_RejectsWithConflict()
    {
        // The chat controller should check session status and return 409 for sealed sessions.
        // Currently no such check exists — this test documents the expected behavior.
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("sealed-session", "agent-a");
        session.Status = GatewaySessionStatus.Sealed;
        await store.SaveAsync(session);

        var supervisor = new Mock<IAgentSupervisor>();
        var controller = new ChatController(supervisor.Object, store);

        var result = await controller.Send(
            new ChatRequest("agent-a", "hello", "sealed-session"),
            CancellationToken.None);

        // Should refuse to send to a sealed session
        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Fact]
    public async Task SendMessageToSuspendedSession_ChatController_RejectsWithConflict()
    {
        var store = new InMemorySessionStore();
        var session = await store.GetOrCreateAsync("suspended-session", "agent-a");
        session.Status = GatewaySessionStatus.Suspended;
        await store.SaveAsync(session);

        var supervisor = new Mock<IAgentSupervisor>();
        var controller = new ChatController(supervisor.Object, store);

        var result = await controller.Send(
            new ChatRequest("agent-a", "hello", "suspended-session"),
            CancellationToken.None);

        // Should refuse to send to a suspended session
        result.Result.ShouldBeOfType<ConflictObjectResult>();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Fix 2 — Archive conversation endpoint
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConversationsController_Archive_Returns204()
    {
        var store = new InMemoryConversationStore();
        var conv = await store.CreateAsync(MakeConversation("to-archive"));
        var controller = CreateConvController(store);

        var result = await controller.Archive(conv.ConversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        var loaded = await store.GetAsync(conv.ConversationId);
        loaded.ShouldNotBeNull();
        loaded!.Status.ShouldBe(ConversationStatus.Archived);
    }

    [Fact]
    public async Task ConversationsController_Archive_NotFound_Returns404()
    {
        var store = new InMemoryConversationStore();
        var controller = CreateConvController(store);

        var result = await controller.Archive("nonexistent-id", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }
}
