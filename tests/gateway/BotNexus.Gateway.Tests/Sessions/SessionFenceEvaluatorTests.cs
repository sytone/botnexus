using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;

namespace BotNexus.Gateway.Tests.Sessions;

/// <summary>
/// Unit tests for the issue #1518 fence decision logic in <see cref="SessionFenceEvaluator"/>.
/// This is the single source of truth every fenced-save implementation delegates to, so its three
/// rebound branches (row deleted, conversation rebound, sealed/expired by reset) are pinned here.
/// </summary>
public sealed class SessionFenceEvaluatorTests
{
    private static SessionWriteFence Fence(string sessionId, ConversationId conversationId)
        => new(SessionId.From(sessionId), conversationId);

    private static GatewaySession SessionRow(string sessionId, ConversationId conversationId, SessionStatus status)
    {
        var session = new GatewaySession(new Session { SessionId = SessionId.From(sessionId) })
        {
            Status = status
        };
        session.ConversationId = conversationId;
        return session;
    }

    [Fact]
    public void Passes_WhenRowMatchesAndActive_ReturnsTrue()
    {
        var conv = ConversationId.Create();
        var fence = Fence("s1", conv);
        var current = SessionRow("s1", conv, SessionStatus.Active);

        SessionFenceEvaluator.Passes(fence, current).ShouldBeTrue();
    }

    [Fact]
    public void Passes_WhenRowDeleted_ReturnsFalse()
    {
        var fence = Fence("s1", ConversationId.Create());

        // A null current row models a delete that landed mid-run.
        SessionFenceEvaluator.Passes(fence, current: null).ShouldBeFalse();
    }

    [Fact]
    public void Passes_WhenConversationRebound_ReturnsFalse()
    {
        var fence = Fence("s1", ConversationId.Create());
        var current = SessionRow("s1", ConversationId.Create(), SessionStatus.Active);

        SessionFenceEvaluator.Passes(fence, current).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SessionStatus.Sealed)]
    [InlineData(SessionStatus.Expired)]
    public void Passes_WhenSealedOrExpiredByReset_ReturnsFalse(SessionStatus status)
    {
        var conv = ConversationId.Create();
        var fence = Fence("s1", conv);
        var current = SessionRow("s1", conv, status);

        SessionFenceEvaluator.Passes(fence, current).ShouldBeFalse();
    }

    [Fact]
    public void Passes_WhenSuspended_ReturnsTrue()
    {
        // Suspended is a resumable state, not a reset/delete signal, so a finalizer write is allowed.
        var conv = ConversationId.Create();
        var fence = Fence("s1", conv);
        var current = SessionRow("s1", conv, SessionStatus.Suspended);

        SessionFenceEvaluator.Passes(fence, current).ShouldBeTrue();
    }

    [Fact]
    public void Passes_WhenCapturedConversationUninitialized_DoesNotFalselyRebound()
    {
        // A run that started before its conversation was stamped has no meaningful conversation to
        // compare; it must still be allowed (guarded only by the deleted/sealed branches). The
        // uninitialized sentinel is read from a fresh session (Vogen forbids constructing it via
        // `default` in user code, VOG009).
        var uninitializedConversationId = new GatewaySession().ConversationId;
        uninitializedConversationId.IsInitialized().ShouldBeFalse("precondition: sentinel is uninitialized");

        var fence = new SessionWriteFence(SessionId.From("s1"), uninitializedConversationId);
        var current = SessionRow("s1", ConversationId.Create(), SessionStatus.Active);

        SessionFenceEvaluator.Passes(fence, current).ShouldBeTrue();
    }
}
