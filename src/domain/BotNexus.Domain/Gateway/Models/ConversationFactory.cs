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
/// (peer converse vs sub-agent supervision vs an agent minting a user-facing conversation).
/// <see cref="ConversationVisibility"/> (#2340) is likewise fixed to
/// <see cref="ConversationVisibility.UserFacing"/> on the channel/cron/webhook paths - a conversation a
/// human or an external system triggered is by definition something the user may see - and is an
/// optional parameter on <see cref="CreateForAgent"/>, the only path that can legitimately mint a
/// runtime bookkeeping thread. The private
/// <see cref="Create"/> core takes all three axes positionally and first, so a new provenance axis can
/// only be added by editing one signature that every caller already funnels through.
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
    /// Canonical title for an agent's default conversation - the general, always-there entry point
    /// the agent answers in when no specific conversation is targeted.
    /// </summary>
    public const string DefaultConversationTitle = "General";

    /// <summary>
    /// Mints an agent's <b>default conversation</b> (issue #2488): the one general, always-there
    /// conversation that is the agent's home, ordered first in the portal, auto-selected when no
    /// conversation is targeted, and exempt from cron retention cleanup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why its own factory.</b> <see cref="CreateForChannel"/> already accepts an <c>isDefault</c>
    /// flag, but no caller in <c>src/**</c> ever passed <c>true</c> - which is precisely how the
    /// capability died silently in #196 while the flag, the column, the DTOs, the ordering and the
    /// UI all stayed in place reading a value nothing could set. A boolean parameter is easy to not
    /// pass; an intent-revealing factory is not. This follows the same principle as the rest of the
    /// seam (#2310): the intent is chosen by <em>which factory you call</em>.
    /// </para>
    /// <para>
    /// Provenance is deliberately identical to <see cref="CreateForChannel"/> -
    /// <see cref="ConversationSource.Channel"/> / <see cref="ConversationKind.HumanAgent"/> /
    /// <see cref="ConversationVisibility.UserFacing"/> - because the default conversation IS the
    /// human-facing channel home; only the <see cref="Conversation.IsDefault"/> stamp differs.
    /// Visibility must stay user-facing or the portal auto-select
    /// (<c>FirstOrDefault(c =&gt; c.IsDefault)</c>) could never see it.
    /// </para>
    /// <para>
    /// <b>The uniqueness invariant is not enforced here.</b> This factory constructs a detached
    /// object and has no view of the agent's existing conversations, so "at most one default per
    /// agent" is enforced at the only layer that can see the set - the router, which checks the
    /// store before minting. Pretending to enforce it here would be a guard that cannot fail.
    /// </para>
    /// </remarks>
    /// <param name="conversationId">Identifier for the new conversation.</param>
    /// <param name="agentId">Agent whose default conversation this is.</param>
    /// <param name="title">Human-readable title; blank values normalise to <c>"General"</c>.</param>
    /// <param name="initiator">Citizen that triggered the mint, when known.</param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static Conversation CreateDefaultForAgent(
        ConversationId conversationId,
        AgentId agentId,
        string? title = null,
        CitizenId? initiator = null,
        DateTimeOffset? timestamp = null)
        => Create(
            ConversationSource.Channel,
            ConversationKind.HumanAgent,
            ConversationVisibility.UserFacing,
            conversationId,
            agentId,
            string.IsNullOrWhiteSpace(title) ? DefaultConversationTitle : title,
            initiator,
            purpose: null,
            instructions: null,
            isDefault: true,
            timestamp);

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
            ConversationVisibility.UserFacing,
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
    /// <param name="sourceId">
    /// The cron job id that owns this run (#2121), stamped into <see cref="Conversation.SourceId"/>
    /// so a client can attribute the conversation to a specific job rather than merely to "a
    /// schedule". Optional because not every schedule-driven mint has a job id in hand - the
    /// heartbeat tick is a schedule, not a cron job - and a null is an honest "originator not
    /// recorded" rather than a fabricated identifier.
    /// </param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static Conversation CreateForCron(
        ConversationId conversationId,
        AgentId agentId,
        string? title = null,
        CitizenId? initiator = null,
        string? sourceId = null,
        DateTimeOffset? timestamp = null)
        => Create(
            ConversationSource.Cron,
            ConversationKind.HumanAgent,
            ConversationVisibility.UserFacing,
            conversationId,
            agentId,
            title,
            initiator,
            purpose: null,
            instructions: null,
            isDefault: false,
            timestamp,
            sourceId: sourceId);

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
    /// <param name="sourceId">
    /// The webhook registration id that minted this conversation (#2121), stamped into
    /// <see cref="Conversation.SourceId"/>. This is what lets source-specific retention and the
    /// portal identify the owning registration from the conversation itself instead of matching on
    /// the title text.
    /// </param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static Conversation CreateForWebhook(
        ConversationId conversationId,
        AgentId agentId,
        string? title = null,
        CitizenId? initiator = null,
        string? sourceId = null,
        DateTimeOffset? timestamp = null)
        => Create(
            ConversationSource.Webhook,
            ConversationKind.HumanAgent,
            ConversationVisibility.UserFacing,
            conversationId,
            agentId,
            title,
            initiator,
            purpose: null,
            instructions: null,
            isDefault: false,
            timestamp,
            sourceId: sourceId);

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
    /// <param name="visibility">
    /// Who may see the conversation (#2340). Defaults to <see cref="ConversationVisibility.UserFacing"/>.
    /// Pass <see cref="ConversationVisibility.InternalHidden"/> for a runtime bookkeeping thread that
    /// must never reach a user's conversation list. This is the ONLY sanctioned way to mark a
    /// conversation hidden; rendering surfaces read the stamped field and never probe the id text.
    /// </param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static Conversation CreateForAgent(
        ConversationKind kind,
        ConversationId conversationId,
        AgentId agentId,
        string? title = null,
        CitizenId? initiator = null,
        string? purpose = null,
        ConversationVisibility visibility = ConversationVisibility.UserFacing,
        DateTimeOffset? timestamp = null)
        => Create(
            ConversationSource.Agent,
            kind,
            visibility,
            conversationId,
            agentId,
            title,
            initiator,
            purpose,
            instructions: null,
            isDefault: false,
            timestamp);

    /// <summary>
    /// Mints a self-retriggering ralph loop conversation (issue #2818): the gateway starts a fresh
    /// session in this conversation each time a turn inside it ends, seeded with the conversation's
    /// current <see cref="Conversation.Instructions"/>, until a gateway-enforced stop condition fires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Instructions are required and validated here.</b> A ralph conversation's instructions ARE its
    /// prompt: with none, iteration 2 has nothing to seed and the loop is dead on arrival, but silently
    /// so - it would simply never re-trigger and look identical to a loop that had finished its work.
    /// Refusing at creation with a message naming the missing field turns a silent non-loop into a
    /// loud construction error (acceptance criterion 1). This is why the check lives in the factory:
    /// the factory is the single sanctioned construction seam (#2310), so there is no other way to
    /// bring an instruction-less ralph conversation into existence.
    /// </para>
    /// <para>
    /// The bounds (<paramref name="config"/>) are stamped into conversation metadata by
    /// <see cref="RalphLoopMetadata"/> together with the loop's initial state, so the single
    /// stop-decision function (<see cref="RalphLoopPolicy.Evaluate"/>) has everything it needs from the
    /// conversation row alone.
    /// </para>
    /// </remarks>
    /// <param name="conversationId">Identifier for the new conversation.</param>
    /// <param name="agentId">Agent that owns and runs the loop.</param>
    /// <param name="instructions">
    /// The loop's instructions - the prompt each iteration is seeded with. Required and non-blank.
    /// </param>
    /// <param name="config">Gateway-enforced stop bounds; defaults to <see cref="RalphLoopConfig.Default"/>.</param>
    /// <param name="title">Human-readable title; blank values normalise to <c>"New conversation"</c>.</param>
    /// <param name="initiator">Citizen that started the loop, when known.</param>
    /// <param name="purpose">Optional persisted description of the loop's intent.</param>
    /// <param name="timestamp">Creation clock reading; defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="instructions"/> is null, empty or whitespace.
    /// </exception>
    public static Conversation CreateForRalph(
        ConversationId conversationId,
        AgentId agentId,
        string? instructions,
        RalphLoopConfig? config = null,
        string? title = null,
        CitizenId? initiator = null,
        string? purpose = null,
        DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            throw new ArgumentException(
                "A ralph conversation requires non-empty 'instructions': they are the prompt each iteration is seeded with.",
                nameof(instructions));
        }

        var now = timestamp ?? DateTimeOffset.UtcNow;
        var conversation = Create(
            ConversationSource.Agent,
            ConversationKind.Ralph,
            ConversationVisibility.UserFacing,
            conversationId,
            agentId,
            title,
            initiator,
            purpose,
            instructions,
            isDefault: false,
            now);

        RalphLoopMetadata.Write(
            conversation,
            config ?? RalphLoopConfig.Default,
            RalphLoopState.Initial with { StartedAt = now });

        return conversation;
    }

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
            ConversationVisibility.UserFacing,
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
    /// The single place a <see cref="Conversation"/> is constructed. All three provenance axes are
    /// required and come first so a future fourth axis is a one-signature change that no caller can
    /// skip.
    /// </summary>
    private static Conversation Create(
        ConversationSource source,
        ConversationKind kind,
        ConversationVisibility visibility,
        ConversationId conversationId,
        AgentId agentId,
        string? title,
        CitizenId? initiator,
        string? purpose,
        string? instructions,
        bool isDefault,
        DateTimeOffset? timestamp,
        ConversationId? parentConversationId = null,
        string? spawningToolCallId = null,
        string? sourceId = null)
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
            // #2121: the second half of the provenance pair. Null for the origins where no minting
            // registry exists (channel / agent), so it is never a fabricated value.
            SourceId = sourceId,
            ParentConversationId = parentConversationId,
            SpawningToolCallId = spawningToolCallId,
            Visibility = visibility
        };
    }
}
