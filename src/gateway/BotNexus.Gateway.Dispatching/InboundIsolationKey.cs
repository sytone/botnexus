using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Dispatching;

/// <summary>
/// The unit of isolation an <see cref="InboundIsolationKey"/> resolved to.
/// Ordered most-specific-wins: <see cref="Conversation"/> beats
/// <see cref="Session"/>, which beats <see cref="Channel"/>.
/// </summary>
public enum InboundIsolationScope
{
    /// <summary>
    /// No conversation or session hint was supplied; isolation falls back to the
    /// legacy channel-type + channel-address composite.
    /// </summary>
    Channel = 0,

    /// <summary>
    /// The transport named a specific session but no conversation. The session is
    /// then the widest state the delivery can mutate, so it is the isolation unit.
    /// </summary>
    Session = 1,

    /// <summary>
    /// The transport named a canonical conversation. This is the strongest and
    /// preferred unit because conversation-level state is what concurrent turns
    /// corrupt.
    /// </summary>
    Conversation = 2
}

/// <summary>
/// The explicit, documented unit of inbound isolation: the key that decides which
/// deliveries are FIFO-serialized against each other and which may run in parallel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Policy (issue #2123).</b> The canonical <b>conversation</b> is the unit of
/// isolation. A conversation owns <c>active_session_id</c>, message history, pending
/// <c>ask_user</c> state, todo/canvas state and finalizer writes. Two agent turns
/// running concurrently in one conversation will stomp all of it, so every delivery
/// naming a conversation is serialized against every other delivery naming that same
/// conversation - regardless of which transport, channel address or webhook
/// registration it arrived through. True parallel processing requires separate
/// conversations; it is never obtained by running concurrent sessions over one.
/// </para>
/// <para>
/// <b>The defect this replaces.</b> Isolation was previously implicit, derived inline
/// as <c>RequestedSessionId ?? "{channelType}:{channelAddress}"</c>. For inbound
/// webhooks no session is requested and the channel address is the registration id,
/// so the key collapsed to <c>webhook:&lt;webhookId&gt;</c>. Two registrations pinned
/// to one conversation therefore got two independent queues and raced that
/// conversation's state. Keying on the conversation removes the whole class of race.
/// </para>
/// <para>
/// <b>Precedence and why.</b> Conversation, then session, then channel composite.
/// The session hint deliberately does <i>not</i> win over the conversation hint: two
/// sessions inside one conversation are exactly the overlap #2123 forbids. Scope
/// prefixes (<c>conversation:</c>, <c>session:</c>, <c>channel:</c>) guarantee a raw
/// conversation id can never alias a raw session id in the queue dictionary.
/// </para>
/// <para>
/// <b>Applies to all webhook response modes.</b> <c>sync</c>, <c>async</c> and
/// <c>callback</c> all route through <see cref="IInboundMessageOrchestrator"/> and so
/// all obey this key; they differ only in when the HTTP response is returned, not in
/// isolation. <c>agentAction:false</c> never runs an agent turn and never enters the
/// queue at all, so it is outside this boundary by construction.
/// </para>
/// </remarks>
/// <param name="Scope">Which unit of isolation this key represents.</param>
/// <param name="Value">
/// The scope-prefixed queue key. Stable for a given logical unit and safe to use as a
/// dictionary key.
/// </param>
public readonly record struct InboundIsolationKey(InboundIsolationScope Scope, string Value)
{
    /// <summary>
    /// Derives the isolation key for an inbound message from its typed routing hints.
    /// </summary>
    /// <param name="message">The inbound transport payload.</param>
    /// <returns>The scope and the scope-prefixed queue key.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <c>null</c>.</exception>
    public static InboundIsolationKey ForMessage(InboundMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var hints = InboundMessageRoutingHints.FromMessage(message);

        // Conversation first: it is the widest state a turn can mutate, so it is the
        // only safe unit when one is named. See the remarks above for #2123.
        if (hints.RequestedConversationId is { } conversationId)
            return new InboundIsolationKey(
                InboundIsolationScope.Conversation, $"conversation:{conversationId.Value}");

        if (hints.RequestedSessionId is { } sessionId)
            return new InboundIsolationKey(
                InboundIsolationScope.Session, $"session:{sessionId.Value}");

        return new InboundIsolationKey(
            InboundIsolationScope.Channel,
            $"channel:{message.ChannelType}:{message.ChannelAddress}");
    }

    /// <summary>Returns the queue key, so the type formats usefully in logs.</summary>
    public override string ToString() => Value;
}
