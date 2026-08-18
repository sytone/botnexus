using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Outcome of <see cref="IInboundDeliveryResolver.ResolveAsync"/>: which delivery mechanism the
/// gateway chose, the intent it was asked for, and whether a turn was running when it decided.
/// </summary>
/// <remarks>
/// The requested mode is retained alongside the resolved one so a caller (and a log line) can tell
/// "you asked to steer and we steered" apart from "you asked to steer but nothing was running, so
/// we queued". Collapsing those two into a bare <c>Queue</c> would make the fallback invisible —
/// the same class of defect as a bounded scan that cannot report its own ceiling.
/// </remarks>
/// <param name="Requested">
/// The mode the transport asked for, verbatim, including <see cref="InboundDeliveryMode.Auto"/>.
/// </param>
/// <param name="Resolved">
/// The mechanism that will actually be used. Never <see cref="InboundDeliveryMode.Auto"/>.
/// </param>
/// <param name="TurnWasActive">
/// Whether a live agent handle for the addressed session was running at decision time. This is the
/// server-owned evidence the decision turns on.
/// </param>
public sealed record InboundDeliveryDecision(
    InboundDeliveryMode Requested,
    InboundDeliveryMode Resolved,
    bool TurnWasActive)
{
    /// <summary>
    /// <see langword="true"/> when the caller asked for a live-turn mechanism (steer or interrupt)
    /// but no turn was running, so the message was downgraded to the queue.
    /// </summary>
    public bool FellBackToQueue =>
        Resolved == InboundDeliveryMode.Queue &&
        Requested is InboundDeliveryMode.Steer or InboundDeliveryMode.Interrupt;

    /// <summary>The default queue outcome for an idle session with no explicit intent.</summary>
    public static InboundDeliveryDecision Queued(InboundDeliveryMode requested, bool turnWasActive) =>
        new(requested, InboundDeliveryMode.Queue, turnWasActive);
}
