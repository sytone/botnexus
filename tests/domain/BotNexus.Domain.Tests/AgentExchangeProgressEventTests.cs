using BotNexus.Domain.AgentExchange;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Tests;

/// <summary>
/// #3176: pins the rendered handoff status line and, critically, that a HALT is textually and
/// structurally distinguishable from a COMPLETION (AC4). Lives in the domain test project because
/// the wording is a domain concern — asserting it here means the contract is proven without
/// standing up channels, stores, or a gateway host.
/// </summary>
public sealed class AgentExchangeProgressEventTests
{
    private static AgentExchangeProgressEvent Event(
        AgentExchangeProgressPhase phase,
        string? reason = null,
        bool withChild = true,
        int? turns = null) => new()
        {
            Phase = phase,
            InitiatorId = AgentId.From("nova"),
            TargetId = AgentId.From("farnsworth"),
            ChildConversationId = withChild ? ConversationId.From("c_child") : null,
            ChildSessionId = withChild ? SessionId.From("s_child") : null,
            Reason = reason,
            Turns = turns
        };

    [Fact]
    public void ToStatusLine_ForStarted_NamesBothAgentsAndTheChildConversation()
    {
        var line = Event(AgentExchangeProgressPhase.Started).ToStatusLine();

        line.ShouldContain("started");
        line.ShouldContain("nova -> farnsworth");
        line.ShouldContain("c_child",
            customMessage: "The whole point of the started event is to hand the reader the child " +
                "conversation id so they can go and read the handoff.");
        line.ShouldContain("s_child");
    }

    [Fact]
    public void ToStatusLine_ForHalted_IsDistinguishableFromCompleted()
    {
        // AC4: a turn-cap/budget halt must not read as a normal completion. Asserting the two
        // strings differ is not enough - a reader must be able to tell WHICH it is, so we assert
        // the halt names itself and does NOT claim completion.
        var completed = Event(AgentExchangeProgressPhase.Completed, "exchangeFinished", turns: 4).ToStatusLine();
        var halted = Event(AgentExchangeProgressPhase.Halted, "maxTurnsReached", turns: 6).ToStatusLine();

        completed.ShouldContain("completed");
        completed.ShouldContain("exchangeFinished");

        halted.ShouldContain("halted");
        halted.ShouldContain("maxTurnsReached");
        halted.ShouldNotContain("completed",
            customMessage: "A halted exchange must never render as a completion - that is the " +
                "exact ambiguity AC4 exists to remove.");
    }

    [Fact]
    public void ToStatusLine_ForFailed_CarriesTheErrorReason()
    {
        var line = Event(AgentExchangeProgressPhase.Failed, "LLM upstream went bang").ToStatusLine();

        line.ShouldContain("failed");
        line.ShouldContain("LLM upstream went bang");
    }

    [Fact]
    public void ToStatusLine_WhenNoChildExchangeExists_StillRendersTheHandoffPair()
    {
        // A pre-admission budget refusal halts before any conversation is minted. The line must
        // still be meaningful rather than emitting a dangling "(conversation )".
        var line = Event(AgentExchangeProgressPhase.Halted, "Daily conversation budget exhausted", withChild: false)
            .ToStatusLine();

        line.ShouldContain("halted");
        line.ShouldContain("nova -> farnsworth");
        line.ShouldContain("Daily conversation budget exhausted");
        // Assert the literal clause opener, not the bare word "conversation" - the halt reason
        // itself legitimately contains that word, so a looser assertion tests the wrong thing.
        line.ShouldNotContain("(conversation",
            customMessage: "With no child conversation the line must omit the conversation clause " +
                "entirely, not render an empty one.");
        line.ShouldNotContain("session ",
            customMessage: "...and likewise no dangling session clause.");
    }

    [Fact]
    public void ToStatusLine_ForCompleted_ReportsTurnCount()
    {
        Event(AgentExchangeProgressPhase.Completed, "singleShot", turns: 2)
            .ToStatusLine()
            .ShouldContain("turns: 2");
    }
}
