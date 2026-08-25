using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Isolation;

namespace BotNexus.Gateway.Tests;

/// <summary>
/// Unit tests for the errored-turn projection.
/// </summary>
/// <remarks>
/// The agent loop settles a failed turn through <c>TurnEndEvent</c>, and
/// <c>MapAgentEvent</c> maps that onto a bare <c>TurnEnd</c> carrying neither the finish reason nor
/// the error message. Nothing else produced <c>AgentStreamEventType.Error</c> for a provider fault,
/// so a 404 for a retired model id reached the client as silence and was reported as an empty
/// assistant completion. These tests pin the projection that restores the signal, and — just as
/// importantly — pin the cases that must NOT produce one.
/// </remarks>
public sealed class InProcessIsolationStrategyMapTurnErrorTests
{
    private const string MessageId = "msg-abc123";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public void MapTurnError_ErroredTurn_ProjectsErrorEventCarryingProviderDetail()
    {
        var evt = TurnEnd(new AssistantAgentMessage(
            string.Empty,
            FinishReason: StopReason.Error,
            ErrorMessage: "Anthropic API error 404: model: claude-3-5-haiku-20241022"));

        var result = InProcessAgentHandle.MapTurnError(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.Error);
        result.ErrorMessage.ShouldBe("Anthropic API error 404: model: claude-3-5-haiku-20241022");
        result.MessageId.ShouldBe(MessageId);
    }

    [Fact]
    public void MapTurnError_ErroredTurnWithNoDetail_StillProjectsAnError()
    {
        // A detail-free provider error is rare, but it must still be distinguishable from a turn
        // that simply produced nothing - that ambiguity is the whole defect.
        var evt = TurnEnd(new AssistantAgentMessage(string.Empty, FinishReason: StopReason.Error));

        var result = InProcessAgentHandle.MapTurnError(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.Error);
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void MapTurnError_AbortedTurn_ProjectsNothing()
    {
        // An aborted turn also carries an ErrorMessage ("Operation aborted"), so keying on a
        // non-empty message rather than the stop reason would promote every ordinary cancellation
        // into a client-visible error.
        var evt = TurnEnd(new AssistantAgentMessage(
            string.Empty,
            FinishReason: StopReason.Aborted,
            ErrorMessage: "Operation aborted"));

        InProcessAgentHandle.MapTurnError(evt, MessageId).ShouldBeNull();
    }

    [Fact]
    public void MapTurnError_NormalTurn_ProjectsNothing()
    {
        var evt = TurnEnd(new AssistantAgentMessage("here is your answer"));

        InProcessAgentHandle.MapTurnError(evt, MessageId).ShouldBeNull();
    }

    [Fact]
    public void MapTurnError_ToolUseTurn_ProjectsNothing()
    {
        var evt = TurnEnd(new AssistantAgentMessage(string.Empty, FinishReason: StopReason.ToolUse));

        InProcessAgentHandle.MapTurnError(evt, MessageId).ShouldBeNull();
    }

    [Fact]
    public void MapTurnError_NonTurnEndEvent_ProjectsNothing()
    {
        var evt = new MessageStartEvent(new AssistantAgentMessage("hi"), Now);

        InProcessAgentHandle.MapTurnError(evt, MessageId).ShouldBeNull();
    }

    [Fact]
    public void MapAgentEvent_ErroredTurn_StillMapsToTurnEnd()
    {
        // The error projection is additive. TurnEnd must survive, because a turn that produced
        // partial output before failing relies on it to flush that content to history.
        var evt = TurnEnd(new AssistantAgentMessage(
            "partial answer",
            FinishReason: StopReason.Error,
            ErrorMessage: "boom"));

        var result = InProcessAgentHandle.MapAgentEvent(evt, MessageId);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(AgentStreamEventType.TurnEnd);
    }

    private static TurnEndEvent TurnEnd(AssistantAgentMessage message) => new(message, [], Now);
}
