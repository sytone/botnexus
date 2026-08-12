using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Conversations;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Services;
using BotNexus.Gateway.Api.Controllers;
using BotNexus.Gateway.Sessions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Shouldly;

namespace BotNexus.Gateway.Tests.Conversations;

/// <summary>
/// Pins archive/delete protection for an agent's default conversation (issue #2488, AC5).
/// </summary>
/// <remarks>
/// <para>
/// <c>CronTrigger</c> already exempts <c>IsDefault</c> conversations from retention cleanup, which
/// encodes the intent that a default is not disposable. That exemption was unreachable while nothing
/// could set the flag; now that #2488 makes defaults real, the REST archive path must honour the same
/// intent or the portal's delete button would silently remove the agent's only home and leave
/// auto-select resolving to null again - the exact regression this issue exists to close.
/// </para>
/// <para>
/// The refusal is a <c>409 Conflict</c> rather than a silent no-op: a DELETE that reports success
/// while changing nothing is indistinguishable from one that worked.
/// </para>
/// </remarks>
public sealed class ConversationsControllerDefaultArchiveGuardTests
{
    private static readonly AgentId TestAgent = AgentId.From("agent-2488");

    private static (ConversationsController Controller, Mock<IConversationStore> Store, Mock<IConversationResetService> Reset)
        CreateController(Conversation conversation)
    {
        var store = new Mock<IConversationStore>();
        store
            .Setup(c => c.GetAsync(conversation.ConversationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(conversation);
        store
            .Setup(c => c.ArchiveAsync(conversation.ConversationId, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reset = new Mock<IConversationResetService>();
        reset
            .Setup(r => r.ResetActiveSessionAsync(conversation.ConversationId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConversationResetResult(ConversationResetOutcome.NoActiveSession, null, TestAgent));

        var controller = new ConversationsController(
            store.Object,
            new InMemorySessionStore(),
            resetService: reset.Object);

        return (controller, store, reset);
    }

    private static Conversation Conversation(bool isDefault) => new()
    {
        ConversationId = ConversationId.From("conv-2488-archive"),
        AgentId = TestAgent,
        IsDefault = isDefault
    };

    // ── Sad path: a default must not be archivable ────────────────────────────

    [Fact]
    public async Task Archive_RefusesToArchiveTheDefaultConversation()
    {
        var (controller, store, reset) = CreateController(Conversation(isDefault: true));

        var result = await controller.Archive("conv-2488-archive", CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();

        // The refusal must be total: no archive, and no destructive session reset either.
        // Sealing the session of a conversation we then refuse to archive would be the worst
        // of both worlds - the visible row survives but its live session is destroyed.
        store.Verify(
            c => c.ArchiveAsync(It.IsAny<ConversationId>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        reset.Verify(
            r => r.ResetActiveSessionAsync(It.IsAny<ConversationId>(), It.IsAny<SessionId?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Happy path: everything else still archives ────────────────────────────

    [Fact]
    public async Task Archive_StillArchivesANonDefaultConversation()
    {
        var (controller, store, reset) = CreateController(Conversation(isDefault: false));

        var result = await controller.Archive("conv-2488-archive", CancellationToken.None);

        result.ShouldBeOfType<NoContentResult>();
        store.Verify(
            c => c.ArchiveAsync(ConversationId.From("conv-2488-archive"), "rest-api", It.IsAny<string?>(), "api", It.IsAny<CancellationToken>()),
            Times.Once);
        reset.Verify(
            r => r.ResetActiveSessionAsync(ConversationId.From("conv-2488-archive"), null, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
