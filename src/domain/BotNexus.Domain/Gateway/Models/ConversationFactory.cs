using BotNexus.Domain.Primitives;
using BotNexus.Domain.World;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// The single sanctioned construction seam for <see cref="Conversation"/> (issue #2310).
/// Every production path that mints a conversation goes through one of the intent-revealing
/// <c>CreateForXxx</c> factories below; direct <c>new Conversation { ... }</c> is banned outside
/// this type by <c>ConversationCreationSeamArchitectureTests</c> (the sole other exception is
/// <c>ConversationRowMapper</c>, which hydrates an already-existing row from persistence rather
/// than creating a conversation).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam.</b> Provenance (<see cref="Conversation.Source"/> / <see cref="Conversation.Kind"/>)
/// was previously stamped by hand at eight independent call sites across five assemblies. Adding a
/// provenance field meant threading it to all eight, and the failure mode was silent: a missed site
/// does not error, it silently takes the enum default (<see cref="ConversationSource.Channel"/>) and
/// produces a conversation that lies about its own origin. Routing every mint through this type makes
/// omission structurally impossible - the origin is chosen by <em>which factory you call</em>, and
/// there is no overload that lets you skip it.
/// </para>
/// <para>
/// <b>Design.</b> Each factory hard-codes the <see cref="ConversationSource"/> it is named for.
/// <see cref="ConversationKind"/> is fixed where the origin path determines it (channel, cron and
/// webhook conversations are always <see cref="ConversationKind.HumanAgent"/>) and is a <em>required</em>
/// parameter on <see cref="CreateForAgent"/>, the one origin where the pairing topology genuinely varies
/// (peer converse vs sub-agent supervision vs an agent minting a user-facing conversation). The private
/// <see cref="Create"/> core takes both axes positionally and first, so a new provenance axis can only be
/// added by editing one signature that every caller already funnels through.
/// </para>
/// <para>
/// <b>Centralised invariants.</b> Id is caller-supplied (several paths need a deterministic id - the
/// heartbeat's stable per-agent id, cron's <c>conv:</c> form), but the creation-time facts that were
/// duplicated at every site now live here exactly once: <c>Status = Active</c>, <c>CreatedAt</c> and
/// <c>UpdatedAt</c> stamped from a single clock read (so they can never disagree), and blank titles
/// normalised to the canonical <c>"New conversation"</c>. Post-construction mutable state
/// (<c>Metadata</c>, <c>ChannelBindings</c>, <c>CanvasHtml</c>, ...) is deliberately not modelled here:
/// it is not provenance, and folding it in would turn the seam into a parameter swamp.
/// </para>
/// </remarks>
public static class ConversationFactory
{
    /// <summary>Canonical placeholder used when a caller supplies no meaningful title.</summary>
    private const string DefaultTitle = "New conversation";

    /// <summary>
    /// Mints a channel-originated conversation: a citizen sent the first inbound message on a channel
    /// binding, or a client explicitly created the conversation through the REST API. Always
    /// <see cref="ConversationKind.HumanAgent"/> - a human is, by definition, one end of this pairing.
    /// </summary>
    /// <param name="conversationId">Identifier for the new conversation.</param>
    /// <param name="agentId">Agent that owns the conversation.</param>
    /// <param name="title">Human-readable title; blank values normalise to <c>"New conversation"</c>.</param>
    /// <param name="initiator">Citizen that opened the conversation, when known.</param>
    /// <param name="purpose">Optional persisted description of the conversation's intent.</param>
    /// <param name="instructions">Optional conversation-scoped system-prompt instructions.</param>
    /// <param name="isDefault">Whether this is the agent's default conversation.</param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static Conversation CreateForChannel(
        ConversationId conversationId,
        AgentId agentId,
        string? title = null,
        CitizenId? initiator = null,
        string? purpose = null,
        string? instructions = null,
        bool isDefault = false,
        DateTimeOffset? timestamp = null)
        => Create(
            ConversationSource.Channel,
            ConversationKind.HumanAgent,
            conversationId,
            agentId,
            title,
            initiator,
            purpose,
            instructions,
            isDefault,
            timestamp);

    /// <summary>
    /// Mints a schedule-originated conversation: a cron job or a heartbeat tick created it for its run.
    /// Explicit provenance is what lets a client badge an unattended scheduled run without sniffing a
    /// <c>cron:</c> prefix out of a session id.
    /// </summary>
    /// <param name="conversationId">Identifier for the new conversation.</param>
    /// <param name="agentId">Agent that owns the conversation.</param>
    /// <param name="title">Human-readable title; blank values normalise to <c>"New conversation"</c>.</param>
    /// <param name="initiator">Citizen credited with the scheduled run.</param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static Conversation CreateForCron(
        ConversationId conversationId,
        AgentId agentId,
        string? title = null,
        CitizenId? initiator = null,
        DateTimeOffset? timestamp = null)
        => Create(
            ConversationSource.Cron,
            ConversationKind.HumanAgent,
            conversationId,
            agentId,
            title,
            initiator,
            purpose: null,
            instructions: null,
            isDefault: false,
            timestamp);

    /// <summary>
    /// Mints a webhook-originated conversation: an external system POSTed to a webhook registration and
    /// the handler pinned a fresh conversation for it. Distinct from
    /// <see cref="CreateForChannel"/> because no human is in the loop, which is precisely what read-only /
    /// composer gating needs to know.
    /// </summary>
    /// <param name="conversationId">Identifier for the new conversation.</param>
    /// <param name="agentId">Agent that owns the conversation.</param>
    /// <param name="title">Human-readable title; blank values normalise to <c>"New conversation"</c>.</param>
    /// <param name="initiator">Citizen credited with the delivery (typically the receiving agent).</param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static Conversation CreateForWebhook(
        ConversationId conversationId,
        AgentId agentId,
        string? title = null,
        CitizenId? initiator = null,
        DateTimeOffset? timestamp = null)
        => Create(
            ConversationSource.Webhook,
            ConversationKind.HumanAgent,
            conversationId,
            agentId,
            title,
            initiator,
            purpose: null,
            instructions: null,
            isDefault: false,
            timestamp);

    /// <summary>
    /// Mints an agent-originated conversation: the <c>conversation_new</c> tool, an agent-to-agent
    /// converse handshake, a cross-world federation relay, or sub-agent supervision.
    /// </summary>
    /// <param name="kind">
    /// Required pairing topology. This is the one origin where <see cref="ConversationKind"/> genuinely
    /// varies - <see cref="ConversationKind.AgentAgent"/> for peer converse and federation relays,
    /// <see cref="ConversationKind.AgentSubAgent"/> for supervision, and
    /// <see cref="ConversationKind.HumanAgent"/> when an agent mints a user-facing conversation via the
    /// <c>conversation_new</c> tool. It is deliberately not defaulted: guessing here is exactly the class
    /// of silent-wrong-provenance bug this seam exists to eliminate.
    /// </param>
    /// <param name="conversationId">Identifier for the new conversation.</param>
    /// <param name="agentId">Agent that owns the conversation.</param>
    /// <param name="title">Human-readable title; blank values normalise to <c>"New conversation"</c>.</param>
    /// <param name="initiator">
    /// Agent that minted the conversation, when it resolves locally. <c>null</c> for cross-world relays,
    /// whose source citizens do not exist in the local registries.
    /// </param>
    /// <param name="purpose">Optional persisted description of the conversation's intent.</param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static Conversation CreateForAgent(
        ConversationKind kind,
        ConversationId conversationId,
        AgentId agentId,
        string? title = null,
        CitizenId? initiator = null,
        string? purpose = null,
        DateTimeOffset? timestamp = null)
        => Create(
            ConversationSource.Agent,
            kind,
            conversationId,
            agentId,
            title,
            initiator,
            purpose,
            instructions: null,
            isDefault: false,
            timestamp);

    /// <summary>
    /// Mints the conversation for a nested sub-agent run: a supervising agent delegated work to a
    /// child, and that run is a conversation in its own right rather than a stream of foreign events
    /// injected into the supervisor's thread (issue #2338).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Distinct from <see cref="CreateForAgent"/> only in that the parent edge is <em>required</em>.
    /// A sub-agent conversation without a parent is not a meaningful state: it would be unreachable
    /// (it is deliberately not surfaced at top level in listings) and there would be nothing to
    /// render its expandable card against. Making the edge a required parameter of the one factory
    /// that mints these conversations is what keeps that guarantee structural rather than advisory.
    /// </para>
    /// <para>
    /// <see cref="ConversationKind"/> is fixed to <see cref="ConversationKind.AgentSubAgent"/> and
    /// <see cref="ConversationSource"/> to <see cref="ConversationSource.Agent"/>: unlike the general
    /// agent origin, the pairing topology here does not vary.
    /// </para>
    /// </remarks>
    /// <param name="conversationId">Identifier for the new child conversation - its own, never the parent's.</param>
    /// <param name="agentId">The child (sub-)agent that owns the conversation.</param>
    /// <param name="parentConversationId">The supervising conversation this run is nested under.</param>
    /// <param name="spawningToolCallId">The parent-side tool call that spawned the run, when known.</param>
    /// <param name="title">Human-readable title; blank values normalise to <c>"New conversation"</c>.</param>
    /// <param name="initiator">The agent that spawned the run, when it resolves locally.</param>
    /// <param name="purpose">Optional persisted description of the run's intent (typically the task).</param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static Conversation CreateForSubAgent(
        ConversationId conversationId,
        AgentId agentId,
        ConversationId parentConversationId,
        string? spawningToolCallId = null,
        string? title = null,
        CitizenId? initiator = null,
        string? purpose = null,
        DateTimeOffset? timestamp = null)
        => Create(
            ConversationSource.Agent,
            ConversationKind.AgentSubAgent,
            conversationId,
            agentId,
            title,
            initiator,
            purpose,
            instructions: null,
            isDefault: false,
            timestamp,
            parentConversationId,
            spawningToolCallId);

    /// <summary>
    /// The single place a <see cref="Conversation"/> is constructed. Both provenance axes are required
    /// and come first so a future third axis is a one-signature change that no caller can skip.
    /// </summary>
    private static Conversation Create(
        ConversationSource source,
        ConversationKind kind,
        ConversationId conversationId,
        AgentId agentId,
        string? title,
        CitizenId? initiator,
        string? purpose,
        string? instructions,
        bool isDefault,
        DateTimeOffset? timestamp,
        ConversationId? parentConversationId = null,
        string? spawningToolCallId = null)
    {
        // One clock read for both stamps: CreatedAt and UpdatedAt must never disagree at birth.
        var now = timestamp ?? DateTimeOffset.UtcNow;

        return new Conversation
        {
            ConversationId = conversationId,
            AgentId = agentId,
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title,
            Purpose = purpose,
            Instructions = instructions,
            IsDefault = isDefault,
            Status = ConversationStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
            Initiator = initiator,
            Kind = kind,
            Source = source,
            ParentConversationId = parentConversationId,
            SpawningToolCallId = spawningToolCallId
        };
    }
}
