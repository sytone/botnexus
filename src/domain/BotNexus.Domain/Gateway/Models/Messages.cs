using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// A message received from a channel adapter, ready for routing to an agent.
/// </summary>
public sealed record InboundMessage
{
    /// <summary>The channel this message arrived from (e.g., "signalr", "telegram").</summary>
    public required ChannelKey ChannelType { get; init; }

    /// <summary>
    /// Channel-native wire token for the sender (connection id, user id from the
    /// underlying transport, envelope sender id, etc.). Used for logging, allow-listing,
    /// fan-out exclusion, and channel-level audit. NOT a typed domain identity —
    /// for that use <see cref="Sender"/>.
    /// </summary>
    public required string SenderId { get; init; }

    /// <summary>
    /// Typed domain identity of the citizen that produced this message, resolved at
    /// the channel boundary. Always populated; never <c>default</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Channel adapters resolve <see cref="Sender"/> from their wire-level
    /// <see cref="SenderId"/> (typically <c>CitizenId.Of(UserId.From(wireToken))</c>).
    /// Synthetic producers (e.g. the conversation tool, sub-agent wake-ups) populate
    /// it directly with the originating agent (<c>CitizenId.Of(agentId)</c>).
    /// </para>
    /// <para>
    /// <see cref="SenderId"/> and <see cref="Sender"/> may differ in shape: for
    /// example the sub-agent wake-up has <c>SenderId = "subagent:&lt;id&gt;"</c> for
    /// audit while <see cref="Sender"/> carries the typed child agent id. Downstream
    /// participant tracking, conversation ownership and species classification must
    /// use <see cref="Sender"/>, never re-parse <see cref="SenderId"/>.
    /// </para>
    /// </remarks>
    public required CitizenId Sender { get; init; }

    /// <summary>
    /// Conversation identifier within the channel (e.g., chat ID, thread ID).
    /// Combined with <see cref="ChannelType"/> to derive a session key.
    /// </summary>
    public required ChannelAddress ChannelAddress { get; init; }

    /// <summary>The message text content.</summary>
    public required string Content { get; init; }

    /// <summary>
    /// Optional multi-modal content parts. When present, the message includes
    /// non-text content (audio, images, etc.) alongside or instead of <see cref="Content"/>.
    /// </summary>
    public IReadOnlyList<MessageContentPart>? ContentParts { get; init; }

    /// <summary>When the message was received.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Extensible metadata from the channel adapter.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// Optional per-message response-mode preference. <c>true</c> requests streaming when the
    /// resolved adapter supports it, <c>false</c> requests one consolidated reply, and
    /// <c>null</c> preserves the adapter's historical default.
    /// </summary>
    public bool? StreamResponse { get; init; }

    /// <summary>
    /// Opaque request identity supplied by the originating channel. The gateway copies this
    /// token to source-channel replies and stream targets so an adapter can correlate concurrent
    /// requests without interpreting or exposing arbitrary inbound metadata.
    /// </summary>
    public string? ChannelRequestId { get; init; }

    /// <summary>
    /// The channel binding ID this message arrived on, if known.
    /// Used by fan-out to exclude the originating binding from echo.
    /// </summary>
    public BindingId? BindingId { get; init; }

    /// <summary>
    /// Optional strongly-typed routing hints supplied by the channel adapter
    /// — the agent, session, and/or conversation the client wants to talk to.
    /// Null when the channel has no override (the default for most inbound
    /// traffic; the router resolves the destination from the binding instead).
    /// </summary>
    /// <remarks>
    /// Prior to sub-PR 6.3 (<c>#586</c>) these hints lived on
    /// <see cref="InboundMessage"/> as three weakly-typed <c>string?</c>
    /// fields (<c>TargetAgentId</c>, <c>SessionId</c>, <c>ConversationId</c>).
    /// They were collapsed into a single typed record so every downstream
    /// consumer reads Vogen-typed values and so writers cannot accidentally
    /// supply one override without the others. Read via
    /// <see cref="InboundMessageRoutingHints.FromMessage(InboundMessage)"/>
    /// when the consumer needs to default null to <see cref="InboundMessageRoutingHints.Empty"/>.
    /// </remarks>
    public InboundMessageRoutingHints? RoutingHints { get; init; }

    /// <summary>
    /// Optional proxy-trigger origin for this message. <c>null</c> for normal
    /// channel-driven user messages (the default). Internal triggers stamp the
    /// kind of proxy they are (<see cref="TriggerType.Cron"/>,
    /// <see cref="TriggerType.Soul"/>, <see cref="TriggerType.Heartbeat"/>) so
    /// downstream handlers can fan-out the same trigger onto the persisted
    /// <see cref="SessionEntry.Trigger"/>. See P9-E (#645) / directive G-3.
    /// </summary>
    public TriggerType? Trigger { get; init; }

    /// <summary>
    /// Optional override for the role this message should be recorded under when an
    /// agent posts it into a channel. <c>null</c> (the default) means "no override" --
    /// the role is derived from the sender kind downstream (see #1547 Step 2/3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Foundation for post-as-assistant (<c>#1649</c>, Step 1/3 of <c>#1547</c>). Today
    /// agent-initiated posts (e.g. the conversation tool, cross-channel relays) are
    /// hard-stamped as <see cref="MessageRole.User"/>. Per the Hybrid approach agreed on
    /// <c>#1547</c>, an agent should default to <see cref="MessageRole.Assistant"/> when
    /// speaking as itself, with an explicit <see cref="MessageRole.User"/> override for
    /// the on-behalf-of-user kickoff case. This field carries that intended role onto the
    /// inbound path so the role-stamp step has a value to read.
    /// </para>
    /// <para>
    /// This is purely additive plumbing: no consumer reads <see cref="SpeakAs"/> yet, so
    /// the field has no behavioural effect on its own. Step 2/3 (<c>#1650</c>) wires the
    /// role derivation; Step 3/3 (<c>#1651</c>) makes the render honour the assistant role.
    /// </para>
    /// </remarks>
    public MessageRole? SpeakAs { get; init; }

    /// <summary>
    /// Derives the <see cref="MessageRole"/> this message should be recorded under when it
    /// is stamped into channel/session history, applying the Hybrid rule agreed on <c>#1547</c>
    /// (Step 2/3, <c>#1650</c>). Called by every role-stamp site on the inbound agent-post path
    /// so the local copy a producer writes and the entry the gateway persists never diverge.
    /// </summary>
    /// <remarks>
    /// The rule, in priority order:
    /// <list type="number">
    /// <item>An explicit <see cref="SpeakAs"/> override is honoured verbatim (e.g. an
    /// on-behalf-of-user kickoff posting <see cref="MessageRole.User"/>).</item>
    /// <item>Otherwise an agent sender (<see cref="CitizenKind.Agent"/>) defaults to
    /// <see cref="MessageRole.Assistant"/> -- the agent speaking as itself.</item>
    /// <item>Otherwise (a human <see cref="CitizenKind.User"/> sender) the role stays
    /// <see cref="MessageRole.User"/>, unchanged from the pre-Hybrid behaviour.</item>
    /// </list>
    /// The override is unconditional so a caller-supplied role is never silently discarded,
    /// even for a user-kind sender.
    /// </remarks>
    public MessageRole DeriveChannelPostRole() =>
        SpeakAs
        ?? (Sender.Kind == CitizenKind.Agent ? MessageRole.Assistant : MessageRole.User);

    /// <summary>
    /// Orthogonal, typed presentation/delivery kind for this message (issue #2149). <c>null</c>
    /// (the default) means "no explicit kind" and is treated as <see cref="MessageKind.Message"/>
    /// downstream. The sub-agent completion dispatch stamps
    /// <see cref="MessageKind.SubAgentCompletion"/> here so the gateway can persist the inbound
    /// completion entry - and the resulting parent turn - with a distinct kind instead of forcing
    /// channels to re-parse <see cref="SenderId"/>, <see cref="Metadata"/>, or the textual
    /// completion envelope. Kept separate from <see cref="SpeakAs"/>/<see cref="MessageRole"/>: the
    /// role stays the LLM/conversation role; the kind carries presentation semantics.
    /// </summary>
    public MessageKind? Kind { get; init; }

    /// <summary>
    /// Resolves the effective <see cref="MessageKind"/> for this message, mapping the unset
    /// <see cref="Kind"/> to <see cref="MessageKind.Message"/> so every consumer reads a concrete,
    /// non-null kind and legacy/unstamped messages default safely.
    /// </summary>
    /// <returns>The stamped kind, or <see cref="MessageKind.Message"/> when none was supplied.</returns>
    public MessageKind ResolveKind() => Kind ?? MessageKind.Message;
}

/// <summary>
/// A message to send back through a channel adapter.
/// </summary>
public sealed record OutboundMessage
{
    /// <summary>The channel to send through.</summary>
    public required ChannelKey ChannelType { get; init; }

    /// <summary>Target conversation identifier within the channel.</summary>
    public required ChannelAddress ChannelAddress { get; init; }

    /// <summary>The message content.</summary>
    public required string Content { get; init; }

    /// <summary>The session this message belongs to.</summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// The conversation this message belongs to. Optional for back-compat, but channel
    /// adapters that group by conversation (e.g. SignalR) prefer this over
    /// <see cref="SessionId"/> so the same group continues to receive deliveries across
    /// session compaction within the conversation.
    /// </summary>
    public string? ConversationId { get; init; }

    /// <summary>
    /// Stable identifier of the channel binding that this message is being sent to.
    /// Populated during fan-out to allow adapters and logging to correlate delivery.
    /// </summary>
    public BindingId? BindingId { get; init; }

    /// <summary>
    /// Optional display prefix prepended to outbound content when
    /// <see cref="BotNexus.Gateway.Abstractions.Models.ThreadingMode.Prefix"/> is in use.
    /// Null when no prefix is needed.
    /// </summary>
    public string? DisplayPrefix { get; init; }

    /// <summary>Extensible metadata for the channel adapter.</summary>
    public IReadOnlyDictionary<string, object?> Metadata { get; init; } =
        new Dictionary<string, object?>();

    /// <summary>
    /// Opaque request identity copied from the originating inbound message. Source adapters use
    /// it to recover exact transport reply context when requests sharing an address complete out
    /// of order. Observer fan-out messages leave it <c>null</c>.
    /// </summary>
    public string? ChannelRequestId { get; init; }

    /// <summary>
    /// Optional role the delivered content should be recorded/rendered under. <c>null</c>
    /// (the default) means the surface applies its own default -- for the SignalR portal that
    /// is the assistant bubble, matching every pre-#1651 outbound post. An explicit value
    /// carries the role stamped on the inbound agent-post (see
    /// <see cref="InboundMessage.DeriveChannelPostRole"/>) all the way to the live fan-out so a
    /// post the gateway recorded as <see cref="MessageRole.User"/> (an on-behalf-of-user
    /// kickoff) renders as a user bubble rather than being forced to assistant on the wire.
    /// </summary>
    /// <remarks>
    /// Step 3/3 of post-as-assistant (<c>#1651</c>, epic <c>#1547</c>). Step 2 (<c>#1650</c>)
    /// stamps the role onto the persisted session entry, which already reaches the portal with
    /// the correct role via history replay. This field closes the remaining seam: the live
    /// SignalR fan-out DTO previously dropped the role, so the streamed/buffered path could only
    /// ever flush an assistant bubble. Purely additive -- adapters that ignore it keep their
    /// prior behaviour.
    /// </remarks>
    public MessageRole? SpeakAs { get; init; }

    /// <summary>
    /// Orthogonal, typed presentation/delivery kind exposed to channel adapters (issue #2149).
    /// <c>null</c> (the default) is treated as <see cref="MessageKind.Message"/>. Carries the kind
    /// stamped on the originating entry all the way to live delivery so an adapter can decide
    /// whether to suppress, collapse, or specially render a <see cref="MessageKind.SubAgentCompletion"/>
    /// notification or a <see cref="MessageKind.SubAgentResponse"/> without inferring it from role,
    /// ids, or text. Purely additive - adapters that ignore it keep their prior behaviour.
    /// </summary>
    public MessageKind? Kind { get; init; }

    /// <summary>
    /// Resolves the effective <see cref="MessageKind"/> for delivery, mapping the unset
    /// <see cref="Kind"/> to <see cref="MessageKind.Message"/> so adapters always read a concrete
    /// kind and pre-#2149 outbound posts default safely.
    /// </summary>
    /// <returns>The stamped kind, or <see cref="MessageKind.Message"/> when none was supplied.</returns>
    public MessageKind ResolveKind() => Kind ?? MessageKind.Message;
}
