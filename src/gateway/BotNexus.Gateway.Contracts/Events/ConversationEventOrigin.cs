using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Events;

/// <summary>
/// Immutable provenance and correlation context attached to every published
/// <see cref="ConversationEvent"/>.
/// <para>
/// This exists so the publisher can stay channel-agnostic while sinks still avoid echo
/// loops: a sink compares the origin binding against its own bindings and declines to
/// re-deliver a fact to the exact place it came from. The publisher itself never reads
/// these values.
/// </para>
/// </summary>
/// <param name="BindingId">Binding the originating input arrived on, when the fact was channel-initiated.</param>
/// <param name="UserId">Citizen who triggered the fact, when a human initiated it.</param>
/// <param name="CorrelationId">
/// Identifier tying this fact to the inbound request or run that caused it, for tracing a
/// message end-to-end across gateway and extension logs.
/// </param>
public sealed record ConversationEventOrigin(
    BindingId? BindingId = null,
    UserId? UserId = null,
    string? CorrelationId = null)
{
    /// <summary>
    /// Origin for facts the gateway raises on its own behalf (timers, compaction, system
    /// lifecycle) where no inbound channel, user, or request caused them.
    /// </summary>
    public static ConversationEventOrigin None { get; } = new();
}
