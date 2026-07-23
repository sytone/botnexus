using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>
/// Behavioural tests for <see cref="ConversationsController.Archive"/> and
/// <see cref="ConversationsController.Reset"/>, focused on the F-2c bug (REST archive used to
/// skip the memory-flush bridge) and the new <see cref="IConversationResetService"/> delegation.
/// </summary>
public sealed class ConversationsControllerResetTests
{
    private static readonly AgentId TestAgent = AgentId.From("agent-a");

    [Fact]
    public async Task Archive_DelegatesToResetService_BeforeArchivingConversation()
    {
        var conversationId = ConversationId.From("conv-archive-1");
        var sessions = new InMemorySessionStore();
        var conversationStore = new Mock<IConversationStore>();
        var conversation = new Conversation
        {
            ConversationId = conversationId,
            AgentId = TestAgent,
            ActiveSessionId = SessionId.From("session-active"),
        };
        conversationStore.Setup(c => c.GetAsync(conversationId, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        conversationStore.Setup(c => c.ArchiveAsync(conversationId, "rest-api", It.IsAny<string?>(), "api", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var resetService = new Mock<IConversationResetService>();
        resetService
            .Setup(r => r.ResetActiveSessionAsync(conversationId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResetResult(ConversationResetOutcome.Reset, SessionId.From("session-active"), TestAgent));

        var sequence = new List<string>();
        resetService
            .Setup(r => r.ResetActiveSessionAsync(conversationId, null, It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("reset"))
            .ReturnsAsync(new ConversationResetResult(ConversationResetOutcome.Reset, SessionId.From("session-active"), TestAgent));
        conversationStore
            .Setup(c => c.ArchiveAsync(conversationId, "rest-api", It.IsAny<string?>(), "api", It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("archive"))
            .Returns(Task.CompletedTask);

        var controller = new ConversationsController(
            conversationStore.Object,
            sessions,
            resetService: resetService.Object);

        var result = await controller.Archive(conversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        sequence.ShouldBe(new[] { "reset", "archive" });
        resetService.Verify(r => r.ResetActiveSessionAsync(conversationId, null, It.IsAny<CancellationToken>()), Times.Once);
        conversationStore.Verify(c => c.ArchiveAsync(conversationId, "rest-api", It.IsAny<string?>(), "api", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Archive_DoesNotCallSessionStoreListAsync()
    {
        var conversationId = ConversationId.From("conv-no-walk");
        var conversation = new Conversation { ConversationId = conversationId, AgentId = TestAgent, ActiveSessionId = SessionId.From("session-active") };
        var conversationStore = new Mock<IConversationStore>();
        conversationStore.Setup(c => c.GetAsync(conversationId, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        conversationStore.Setup(c => c.ArchiveAsync(conversationId, "rest-api", It.IsAny<string?>(), "api", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        // GetAsync may be called for virtual cron lookup; permit it returning null.
        sessions.Setup(s => s.GetAsync(It.IsAny<SessionId>(), It.IsAny<CancellationToken>())).ReturnsAsync((GatewaySession?)null);

        var resetService = new Mock<IConversationResetService>();
        resetService.Setup(r => r.ResetActiveSessionAsync(conversationId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResetResult(ConversationResetOutcome.Reset, SessionId.From("session-active"), TestAgent));

        var controller = new ConversationsController(conversationStore.Object, sessions.Object, resetService: resetService.Object);
        var result = await controller.Archive(conversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        // The old SealConversationSessionsAsync walked every session in the store via ListAsync.
        // The new flow trusts ActiveSessionId via the reset service — verify the walk is gone.
        sessions.Verify(
            s => s.ListAsync(It.IsAny<AgentId?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Archive_ResetThrowsTaskCanceled_StillArchivesAndReturns204()
    {
        // Issue #1696: a slow active-session reset (supervisor-stop or memory-flush stall) used to
        // surface as TaskCanceledException and bubble out as a 500. The reset is best-effort, so the
        // conversation must still archive and DELETE must return 204 even when reset is cancelled.
        var conversationId = ConversationId.From("conv-reset-canceled");
        var conversation = new Conversation { ConversationId = conversationId, AgentId = TestAgent, ActiveSessionId = SessionId.From("session-active") };
        var conversationStore = new Mock<IConversationStore>();
        conversationStore.Setup(c => c.GetAsync(conversationId, It.IsAny<CancellationToken>())).ReturnsAsync(conversation);
        conversationStore.Setup(c => c.ArchiveAsync(conversationId, "rest-api", It.IsAny<string?>(), "api", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var resetService = new Mock<IConversationResetService>();
        resetService
            .Setup(r => r.ResetActiveSessionAsync(conversationId, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException());

        var controller = new ConversationsController(
            conversationStore.Object,
            new InMemorySessionStore(),
            resetService: resetService.Object);

        var result = await controller.Archive(conversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        conversationStore.Verify(c => c.ArchiveAsync(conversationId, "rest-api", It.IsAny<string?>(), "api", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Archive_UnknownConversation_Returns404()
    {
        var conversationStore = new Mock<IConversationStore>();
        conversationStore.Setup(c => c.GetAsync(It.IsAny<ConversationId>(), It.IsAny<CancellationToken>())).ReturnsAsync((Conversation?)null);
        var sessions = new InMemorySessionStore();
        var resetService = new Mock<IConversationResetService>(MockBehavior.Strict);

        var controller = new ConversationsController(conversationStore.Object, sessions, resetService: resetService.Object);
        var result = await controller.Archive("nope", CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
        resetService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reset_ExistingConversation_Returns200_WithOutcomeAndSealedSessionId()
    {
        var conversationId = ConversationId.From("conv-reset-1");
        var sealedSessionId = SessionId.From("session-being-sealed");
        var resetService = new Mock<IConversationResetService>();
        resetService.Setup(r => r.ResetActiveSessionAsync(conversationId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResetResult(ConversationResetOutcome.Reset, sealedSessionId, TestAgent));

        var controller = new ConversationsController(
            new Mock<IConversationStore>().Object,
            new InMemorySessionStore(),
            resetService: resetService.Object);

        var result = await controller.Reset(conversationId.Value, CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var payload = ok.Value.ShouldBeOfType<ConversationResetResponse>();
        payload.ConversationId.ShouldBe(conversationId.Value);
        payload.Outcome.ShouldBe("Reset");
        payload.SealedSessionId.ShouldBe(sealedSessionId.Value);
    }

    [Fact]
    public async Task Reset_NoActiveSession_Returns200_WithNullSealedSessionId()
    {
        var conversationId = ConversationId.From("conv-quiet");
        var resetService = new Mock<IConversationResetService>();
        resetService.Setup(r => r.ResetActiveSessionAsync(conversationId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResetResult(ConversationResetOutcome.NoActiveSession, null, TestAgent));

        var controller = new ConversationsController(
            new Mock<IConversationStore>().Object,
            new InMemorySessionStore(),
            resetService: resetService.Object);

        var result = await controller.Reset(conversationId.Value, CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        var payload = ok.Value.ShouldBeOfType<ConversationResetResponse>();
        payload.Outcome.ShouldBe("NoActiveSession");
        payload.SealedSessionId.ShouldBeNull();
    }

    [Fact]
    public async Task Reset_UnknownConversation_Returns404()
    {
        var conversationId = ConversationId.From("conv-nope");
        var resetService = new Mock<IConversationResetService>();
        resetService.Setup(r => r.ResetActiveSessionAsync(conversationId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResetResult(ConversationResetOutcome.NotFound, null, null));

        var controller = new ConversationsController(
            new Mock<IConversationStore>().Object,
            new InMemorySessionStore(),
            resetService: resetService.Object);

        var result = await controller.Reset(conversationId.Value, CancellationToken.None);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Reset_ResetServiceNotRegistered_Returns503()
    {
        var controller = new ConversationsController(
            new Mock<IConversationStore>().Object,
            new InMemorySessionStore()); // resetService omitted

        var result = await controller.Reset("anything", CancellationToken.None);

        var status = result.ShouldBeOfType<ObjectResult>();
        status.StatusCode.ShouldBe(StatusCodes.Status503ServiceUnavailable);
    }
}
