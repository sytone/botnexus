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
    AgentSubAgent = 2
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
