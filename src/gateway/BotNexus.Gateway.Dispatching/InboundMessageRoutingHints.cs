using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// Strongly-typed projection of the routing-hint subset of an inbound transport
/// payload — the three optional override fields a channel may carry to tell the
/// gateway "the client wants to talk to this agent, in this session, in this
/// conversation". Constructed once per inbound message via
/// <see cref="FromMessage(InboundMessage)"/>; consumers downstream of routing read
/// the Vogen-typed properties instead of re-parsing the legacy
/// <c>string?</c> fields on <see cref="InboundMessage"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type is the sanctioned single reader of the legacy
/// <see cref="InboundMessage.TargetAgentId"/>,
/// <see cref="InboundMessage.SessionId"/>, and
/// <see cref="InboundMessage.ConversationId"/> fields. The architecture fence in
/// <c>InboundMessageOverrideFenceTests</c> allowlists exactly this file (plus the
/// three out-of-scope <c>OutboundMessage</c> adapter readers); every other
/// production consumer routes through these typed hints. See umbrella
/// <c>#579</c> / Phase 6 / F-10 — sub-PR 6.1 (<c>#580</c>) added typed properties
/// to <see cref="InboundMessageContext"/>; sub-PR 6.2 (<c>#582</c>) introduced this
/// helper so the remaining reader sites (<c>DefaultMessageRouter</c>,
/// <c>GatewayHost</c>) could migrate without depending on a fully-constructed
/// context (the router runs before <see cref="InboundMessageContext.AgentId"/> is
/// known). Sub-PR 6.3 deletes the legacy <see cref="InboundMessage"/> fields and
/// this type becomes the typed factory at the channel-adapter boundary.
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
/// Optional explicit target agent supplied by the transport. Equivalent to the
/// pre-#580 <see cref="InboundMessage.TargetAgentId"/> string field.
/// </param>
/// <param name="RequestedSessionId">
/// Optional explicit session resume target. Equivalent to the pre-#580
/// <see cref="InboundMessage.SessionId"/> string field.
/// </param>
/// <param name="RequestedConversationId">
/// Optional explicit conversation target supplied by the transport. Equivalent to
/// the pre-#580 <see cref="InboundMessage.ConversationId"/> string field.
/// </param>
public sealed record InboundMessageRoutingHints(
    AgentId? RequestedAgentId,
    SessionId? RequestedSessionId,
    ConversationId? RequestedConversationId)
{
    /// <summary>
    /// An "all-empty" hint payload. Useful as a default for code paths where no
    /// routing hint is meaningful (e.g. internal wake messages that already know
    /// their target session).
    /// </summary>
    public static InboundMessageRoutingHints Empty { get; } = new(null, null, null);

    /// <summary>
    /// Lifts the three legacy routing-hint fields from an inbound transport
    /// payload into their Vogen-typed equivalents. Empty and whitespace inputs
    /// normalise to <c>null</c> rather than throwing, because adapters
    /// historically populate the legacy fields with empty strings.
    /// </summary>
    /// <param name="message">Inbound transport payload.</param>
    /// <returns>Typed routing hints; never <c>null</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <c>null</c>.</exception>
    public static InboundMessageRoutingHints FromMessage(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new InboundMessageRoutingHints(
            TryLiftAgentId(message.TargetAgentId),
            TryLiftSessionId(message.SessionId),
            TryLiftConversationId(message.ConversationId));
    }

    private static ConversationId? TryLiftConversationId(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : ConversationId.From(raw);

    private static AgentId? TryLiftAgentId(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : AgentId.From(raw);

    private static SessionId? TryLiftSessionId(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : SessionId.From(raw);
}
