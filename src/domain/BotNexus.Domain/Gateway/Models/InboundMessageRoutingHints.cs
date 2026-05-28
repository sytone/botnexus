using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Strongly-typed routing-hint payload an inbound channel adapter may carry to
/// tell the gateway "the client wants to talk to this agent, in this session, in
/// this conversation". Every field is Vogen-typed and optional. Constructed
/// either directly by typed writers, via <see cref="LiftFromStrings"/> by
/// string-sourced writers, or read once per inbound message by
/// <see cref="FromMessage(InboundMessage)"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type lives in <c>BotNexus.Domain</c> (the assembly that owns
/// <see cref="InboundMessage"/>) so it can be both a property of the message
/// and a constructor input at every channel-adapter writer site without
/// requiring those adapters to take a project reference on
/// <c>BotNexus.Gateway.Dispatching</c>.
/// </para>
/// <para>
/// Sub-PR 6.1 (<c>#580</c>) added typed properties to
/// <c>InboundMessageContext</c>; sub-PR 6.2 (<c>#582</c>) introduced this
/// helper as the sanctioned single reader so the remaining string-field reader
/// sites (<c>DefaultMessageRouter</c>, <c>GatewayHost</c>) could migrate
/// without depending on a fully-constructed context. Sub-PR 6.3 (<c>#586</c>)
/// promoted the helper to a first-class property on <see cref="InboundMessage"/>
/// (<see cref="InboundMessage.RoutingHints"/>) and deleted the legacy
/// <c>string?</c> fields, at which point <see cref="FromMessage"/> collapsed
/// to a one-line accessor and the lift helpers became writer-side normalisers.
/// </para>
/// <para>
/// The lift is intentionally lenient: <c>null</c>, empty, and whitespace inputs
/// normalise to a <c>null</c> hint. Channel adapters have historically populated
/// the legacy fields with empty strings, and the Vogen <c>From()</c> factories
/// reject whitespace — the lift must absorb that mismatch so the inbound pipeline
/// does not crash on adapter-supplied empties.
/// </para>
/// </remarks>
/// <param name="RequestedAgentId">
/// Optional explicit target agent supplied by the transport.
/// </param>
/// <param name="RequestedSessionId">
/// Optional explicit session resume target supplied by the transport.
/// </param>
/// <param name="RequestedConversationId">
/// Optional explicit conversation target supplied by the transport.
/// </param>
public sealed record InboundMessageRoutingHints(
    AgentId? RequestedAgentId,
    SessionId? RequestedSessionId,
    ConversationId? RequestedConversationId)
{
    /// <summary>
    /// An "all-empty" hint payload. Useful as a default for code paths where no
    /// routing hint is meaningful (e.g. internal wake messages that already know
    /// their target session) and as the fallback return value of
    /// <see cref="FromMessage(InboundMessage)"/> when the inbound message carries
    /// no <see cref="InboundMessage.RoutingHints"/>.
    /// </summary>
    public static InboundMessageRoutingHints Empty { get; } = new(null, null, null);

    /// <summary>
    /// Reads the typed routing hints from an inbound transport payload.
    /// Returns the message's <see cref="InboundMessage.RoutingHints"/> if set,
    /// otherwise <see cref="Empty"/>. The caller never has to null-check the
    /// returned record.
    /// </summary>
    /// <param name="message">Inbound transport payload.</param>
    /// <returns>Typed routing hints; never <c>null</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <c>null</c>.</exception>
    public static InboundMessageRoutingHints FromMessage(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.RoutingHints ?? Empty;
    }

    /// <summary>
    /// Lifts three optional strings from a channel-adapter writer site into a
    /// typed <see cref="InboundMessageRoutingHints"/> record. Each input is
    /// normalised: <c>null</c> / empty / whitespace becomes a <c>null</c> hint
    /// for that slot. Returns <c>null</c> when all three inputs normalise to
    /// <c>null</c> so writers can assign the result directly to
    /// <see cref="InboundMessage.RoutingHints"/> without a separate
    /// "is there anything to set?" check.
    /// </summary>
    /// <remarks>
    /// Non-whitespace inputs that fail Vogen validation (for example an agent id
    /// containing a forbidden character) will throw via the Vogen
    /// <c>From(...)</c> factory. Channel adapters are responsible for validating
    /// their wire formats before reaching this helper; the helper only absorbs
    /// the null/empty/whitespace mismatch between channel conventions and
    /// Vogen primitives. The name uses <c>Lift</c> (not <c>Try</c>) because
    /// invalid non-blank inputs propagate the underlying validation exception
    /// rather than returning a failure result.
    /// </remarks>
    /// <param name="targetAgentId">Raw transport target-agent id, if any.</param>
    /// <param name="sessionId">Raw transport session id, if any.</param>
    /// <param name="conversationId">Raw transport conversation id, if any.</param>
    /// <returns>
    /// A typed hint record, or <c>null</c> if all three inputs are null/empty/whitespace.
    /// </returns>
    public static InboundMessageRoutingHints? LiftFromStrings(
        string? targetAgentId,
        string? sessionId,
        string? conversationId)
    {
        var agent = LiftAgentId(targetAgentId);
        var session = LiftSessionId(sessionId);
        var conversation = LiftConversationId(conversationId);
        if (agent is null && session is null && conversation is null)
        {
            return null;
        }

        return new InboundMessageRoutingHints(agent, session, conversation);
    }

    private static ConversationId? LiftConversationId(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : ConversationId.From(raw);

    private static AgentId? LiftAgentId(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : AgentId.From(raw);

    private static SessionId? LiftSessionId(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : SessionId.From(raw);
}
