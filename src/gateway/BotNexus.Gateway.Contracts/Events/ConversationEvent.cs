using System.Collections.Immutable;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Events;

/// <summary>
/// Base of the channel-neutral gateway conversation event family (issue #2085).
/// <para>
/// The gateway publishes facts about a conversation - an agent produced a token, a
/// conversation was created, a binding was attached - and every registered channel
/// extension decides for itself whether it has an interested recipient. The event
/// therefore carries no channel name, no transport handle, and no routing decision:
/// it is a statement about the conversation, not an instruction to a channel.
/// </para>
/// <para>
/// The family is deliberately a closed set of strongly typed cases rather than one
/// warehouse enum, so agent, conversation-lifecycle, and binding concerns cannot silently
/// leak into each other's payloads. Derivation is restricted to this assembly.
/// </para>
/// </summary>
public abstract record ConversationEvent
{
    /// <summary>Agent that owns the conversation this fact belongs to.</summary>
    public required AgentId AgentId { get; init; }

    /// <summary>Conversation the fact belongs to. This is the ordering key for publication.</summary>
    public required ConversationId ConversationId { get; init; }

    /// <summary>
    /// Session that produced the fact, when one applies. Null for conversation-scoped
    /// lifecycle facts that exist independently of any run (e.g. creation, archival).
    /// </summary>
    public SessionId? SessionId { get; init; }

    /// <summary>
    /// Where the fact came from and what it correlates to, so a sink can suppress echoing
    /// an event back to the origin it arrived on without the publisher knowing channels.
    /// </summary>
    public ConversationEventOrigin Origin { get; init; } = ConversationEventOrigin.None;

    /// <summary>
    /// Immutable snapshot of the conversation's channel bindings as they stood when the
    /// event was raised. A sink inspects this to decide whether it holds a recipient; it is
    /// a value snapshot precisely so one sink cannot mutate what another sink observes.
    /// </summary>
    public ImmutableArray<ConversationBindingSnapshot> Bindings { get; init; }
        = ImmutableArray<ConversationBindingSnapshot>.Empty;

    /// <summary>When the underlying fact occurred, as observed by the gateway.</summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Restricts the case set to this assembly so the family stays closed and exhaustive.</summary>
    private protected ConversationEvent()
    {
    }
}

/// <summary>
/// An agent stream fact projected onto a conversation. Carries the authoritative
/// <see cref="AgentStreamEvent"/> <b>unchanged</b> - the publisher never rewrites, wraps, or
/// re-types the payload, so channel extensions and the agent loop cannot drift apart.
/// </summary>
public sealed record ConversationAgentEvent : ConversationEvent
{
    /// <summary>
    /// The authoritative agent stream event, passed through by reference. Sinks must treat
    /// it as shared immutable state and never attempt to mutate or reinterpret it.
    /// </summary>
    public required AgentStreamEvent StreamEvent { get; init; }
}

/// <summary>Raised once a conversation has been created and is addressable.</summary>
public sealed record ConversationCreatedEvent : ConversationEvent
{
    /// <summary>Display title at creation time, when the creator supplied one.</summary>
    public string? Title { get; init; }
}

/// <summary>Raised when durable conversation metadata (title, purpose, visibility, ...) changed.</summary>
public sealed record ConversationUpdatedEvent : ConversationEvent
{
    /// <summary>
    /// Names of the conversation fields that changed, so a sink can refresh selectively
    /// instead of reloading the whole conversation.
    /// </summary>
    public ImmutableArray<string> ChangedFields { get; init; } = ImmutableArray<string>.Empty;
}

/// <summary>Raised when a conversation is archived and should stop receiving live traffic.</summary>
public sealed record ConversationArchivedEvent : ConversationEvent;

/// <summary>Raised when a channel binding is attached to a conversation.</summary>
public sealed record ConversationBindingAddedEvent : ConversationEvent
{
    /// <summary>The binding that was attached.</summary>
    public required ConversationBindingSnapshot Binding { get; init; }
}

/// <summary>Raised when a channel binding is detached from a conversation.</summary>
public sealed record ConversationBindingRemovedEvent : ConversationEvent
{
    /// <summary>The binding that was detached.</summary>
    public required ConversationBindingSnapshot Binding { get; init; }
}
