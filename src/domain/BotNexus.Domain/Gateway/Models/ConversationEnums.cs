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
/// composite <see cref="ChannelAddress"/> encoding and does not need a mode here.
/// </summary>
public enum ThreadingMode
{
    /// <summary>One conversation per channel address (DMs, SMS).</summary>
    Single,

    /// <summary>The conversation name is prefixed on messages (iMessage fallback, SMS multi-conversation).</summary>
    Prefix
}
