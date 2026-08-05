namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Lifecycle status of a conversation.
/// </summary>
public enum ConversationStatus
{
    /// <summary>The conversation is active and accepts new sessions.</summary>
    Active,

    /// <summary>The conversation has been archived and is read-only.</summary>
    Archived
}

/// <summary>
/// Discriminates the citizen-pairing inside a conversation. Stored on the conversation rather
/// than inferred from session id substrings so the model is authoritative — see plan §4 / F-3.
/// </summary>
public enum ConversationKind
{
    /// <summary>
    /// A human (User citizen) talking to one or more named agents. The historical default,
    /// and the value all pre-Phase-4 conversations deserialize to (kept first so the
    /// enum's default-value contract preserves back-compat).
    /// </summary>
    HumanAgent = 0,

    /// <summary>
    /// Two named agents in a peer exchange (e.g. orchestrator → expert). Created by
    /// <c>IAgentExchangeService.ConverseAsync</c>; the initiator is recorded in
    /// <c>Conversation.Initiator</c> and both citizens appear as session participants.
    /// </summary>
    AgentAgent = 1,

    /// <summary>
    /// A named agent supervising a spawned sub-agent. Inherits the parent conversation
    /// id (see F-6 / PR #547) so sub-agent transcripts are queryable via the parent's
    /// conversation, not a synthetic sub-id.
    /// </summary>
    AgentSubAgent = 2,

    /// <summary>
    /// A self-retriggering autonomous work loop (issue #2818). The conversation owns a set of
    /// instructions; when an agent turn inside it ends, the gateway starts a <em>fresh</em> session
    /// in the same conversation seeded with those instructions, until a gateway-enforced stop
    /// condition fires (see <c>RalphLoopPolicy</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Topologically this is an agent talking to itself, which is why it is a <c>Kind</c> and not a
    /// <see cref="ConversationSource"/>: the trigger is still whatever minted the conversation, and
    /// re-triggering is driven by turn end rather than by a schedule.
    /// </para>
    /// <para>
    /// Added at the END with the next free explicit number. The existing members are persisted
    /// numerically and must never be renumbered.
    /// </para>
    /// </remarks>
    Ralph = 3
}

/// <summary>
/// Discriminates the <em>origination trigger</em> of a conversation - the answer to "why does this
/// conversation exist?" - as a first-class, write-once field rather than something re-derived from
/// session-id string prefixes (<c>cron:</c>), from <c>Conversation.Initiator</c>, or from a
/// hand-synthesized client-side flag. Each surface (portal, mobile, chat channels) previously
/// re-implemented its own bespoke inference to decide how to render, group and gate a conversation;
/// this enum is the shared typed signal that replaces all of it (epic #2300).
/// </summary>
/// <remarks>
/// <para>
/// <b>Orthogonal to <see cref="ConversationKind"/>.</b> <c>Kind</c> encodes the pairing topology
/// (who is talking to whom); <c>Source</c> encodes the trigger (why it exists). A cron run, an
/// inbound webhook run and a user DM are <em>all</em> <see cref="ConversationKind.HumanAgent"/> and
/// are indistinguishable by <c>Kind</c> alone. Together <c>(Source, Kind)</c> fully disambiguate
/// every origination case; <c>Conversation.Initiator</c> answers "which citizen" and
/// <see cref="ConversationStatus"/> answers "what lifecycle stage".
/// </para>
/// <para>
/// <b>Deliberately coarse.</b> <see cref="Agent"/> covers both sub-agent supervision and peer
/// agent-to-agent converse because <c>Kind</c> (<see cref="ConversationKind.AgentSubAgent"/> vs
/// <see cref="ConversationKind.AgentAgent"/>) already separates those two. A fifth value would
/// re-introduce overlap between the two axes and is intentionally not offered.
/// </para>
/// </remarks>
public enum ConversationSource
{
    /// <summary>
    /// User/channel-driven: a human sent the first inbound message on a channel binding (portal,
    /// Telegram, Signal, SMS, ...) or explicitly created the conversation through the REST API.
    /// Kept first so the enum's default-value contract makes this the back-compat value - every
    /// row persisted before this field existed deserializes to <c>Channel</c> with no migration
    /// error, exactly the contract <see cref="ConversationKind.HumanAgent"/> = 0 already uses.
    /// </summary>
    Channel = 0,

    /// <summary>
    /// Schedule-driven: a cron job or heartbeat tick minted the conversation for its run. Lets a
    /// client group or badge unattended scheduled runs without sniffing a <c>cron:</c> prefix out
    /// of a session id.
    /// </summary>
    Cron = 1,

    /// <summary>
    /// Inbound-webhook driven: an external system POSTed to a webhook registration and the handler
    /// pinned a fresh conversation for it. Distinct from <see cref="Channel"/> because no human is
    /// present in the loop, which is what read-only/composer gating actually needs to know.
    /// </summary>
    Webhook = 2,

    /// <summary>
    /// Agent-initiated: an agent minted the conversation itself - via the <c>conversation_new</c>
    /// tool, an agent-to-agent converse handshake, or sub-agent supervision. Use <c>Kind</c> to
    /// tell peer converse (<see cref="ConversationKind.AgentAgent"/>) from sub-agent supervision
    /// (<see cref="ConversationKind.AgentSubAgent"/>).
    /// </summary>
    Agent = 3
}

/// <summary>
/// Discriminates <em>who may see</em> a conversation - the answer to "should this row ever be
/// rendered to a user?" - as a first-class, write-once field rather than something re-derived from
/// a conversation-id string prefix (issue #2340, following epic #2300).
/// </summary>
/// <remarks>
/// <para>
/// <b>A fifth, genuinely orthogonal axis.</b> <see cref="ConversationKind"/> encodes topology (who
/// is talking to whom), <see cref="ConversationSource"/> encodes the trigger (why it exists),
/// <c>Conversation.Initiator</c> encodes identity (which citizen opened it) and
/// <see cref="ConversationStatus"/> encodes lifecycle (active or archived). None of them answers
/// visibility: a runtime bookkeeping thread and a user's own DM can share every one of those four
/// values and still differ on whether the sidebar should show them.
/// </para>
/// <para>
/// <b>Why it exists.</b> Until #2340 the portal answered this by probing the conversation id for an
/// <c>internal:</c> prefix. A conversation id is an opaque identifier; keying behaviour on its text
/// is a hidden coupling between id-minting code and rendering code with nothing enforcing they
/// agree, and the failure mode is silent in both directions (an internal thread appears in the
/// user's sidebar, or a real conversation vanishes from it). This enum is the typed signal that
/// replaces that probe, and it removed the last allowlisted exception from the origin-inference
/// fence.
/// </para>
/// <para>
/// <b>Write-once.</b> Stamped at creation by <c>ConversationFactory</c> and <c>init</c>-only
/// thereafter, exactly like <see cref="ConversationSource"/>. Visibility is a property of what the
/// conversation <em>is</em>, so no later write - and in particular no inbound event - may re-stamp
/// it and make a hidden bookkeeping thread appear, or a user's conversation disappear.
/// </para>
/// </remarks>
public enum ConversationVisibility
{
    /// <summary>
    /// Shown in the user's conversation list and fully interactive (subject to the independent
    /// read-only gating derived from <see cref="ConversationSource"/>/<see cref="ConversationKind"/>).
    /// Kept first so the enum's default-value contract makes this the back-compat value: every row
    /// persisted before this field existed deserializes to <c>UserFacing</c> with no migration
    /// error, the same contract <see cref="ConversationSource.Channel"/> = 0 already uses.
    /// </summary>
    UserFacing = 0,

    /// <summary>
    /// Visible to the user but never writable: an observer/audit view of a conversation the user may
    /// inspect but is not a participant in. Distinct from <see cref="UserFacing"/> so a surface can
    /// render the row while suppressing every write affordance, and distinct from
    /// <see cref="InternalHidden"/> so the row is not silently dropped from the list.
    /// </summary>
    InspectableReadOnly = 1,

    /// <summary>
    /// Runtime bookkeeping that must never be rendered to a user - the internal threads the portal
    /// used to suppress with an <c>internal:</c> id-prefix probe. A conversation carrying this value
    /// is filtered out of user-facing listings entirely.
    /// </summary>
    InternalHidden = 2
}

/// <summary>
/// Controls how a channel binding participates in message fan-out.
/// </summary>
public enum BindingMode
{
    /// <summary>Inbound and outbound — full interactive channel.</summary>
    Interactive,

    /// <summary>Outbound only — the binding receives fan-out but does not originate messages.</summary>
    NotifyOnly,

    /// <summary>No outbound fan-out — the binding is silenced.</summary>
    Muted
}

/// <summary>
/// Controls how a conversation is rendered on the wire for channels that don't
/// natively express multiple conversations on a single address. Native sub-thread
/// routing (e.g. Telegram forum topics) is handled by the channel adapter via
/// composite <c>ChannelAddress</c> encoding and does not need a mode here.
/// </summary>
public enum ThreadingMode
{
    /// <summary>One conversation per channel address (DMs, SMS).</summary>
    Single,

    /// <summary>The conversation name is prefixed on messages (iMessage fallback, SMS multi-conversation).</summary>
    Prefix
}
