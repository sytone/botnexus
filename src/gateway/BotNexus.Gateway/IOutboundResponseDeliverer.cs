using BotNexus.Gateway.Abstractions.Models;
using ConversationId = BotNexus.Domain.Primitives.ConversationId;
using SessionId = BotNexus.Domain.Primitives.SessionId;

namespace BotNexus.Gateway;

/// <summary>
/// Delivers an assistant reply outbound to every fan-out channel binding for a session
/// (every binding except the one the inbound message arrived on).
/// </summary>
/// <remarks>
/// Extracted from <see cref="GatewayHost"/> (#1811) so the outbound fan-out responsibility
/// — resolve bindings, resolve adapter, send, and stale-binding self-heal (demote-to-Muted) —
/// is a focused collaborator that can be unit-tested against a mock
/// <see cref="Abstractions.Channels.IChannelManager"/> /
/// <see cref="Abstractions.Conversations.IConversationRouter"/> without standing up the full
/// 24-dependency inbound turn pipeline. Behaviour is identical to the previous in-host cluster.
/// </remarks>
public interface IOutboundResponseDeliverer
{
    /// <summary>
    /// Fans the assistant reply out to every interactive/notify binding on the session other than
    /// the originating one. No-op when there is nothing to deliver (null/empty content) or no other
    /// bindings exist. Individual binding failures are contained so one bad binding never blocks the
    /// rest; a stale connection self-heals by demoting that binding to
    /// <see cref="Abstractions.Models.BindingMode.Muted"/>.
    /// </summary>
    /// <param name="source">The originating inbound message (its binding is excluded from fan-out).</param>
    /// <param name="sessionId">The session whose bindings to fan out to.</param>
    /// <param name="content">The assistant reply to deliver. When null/empty there is nothing to fan out.</param>
    /// <param name="conversationId">The conversation id, used to demote stale bindings to Muted on self-heal.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="primaryDeliveredToSignalR">
    /// True when the primary (pre-fan-out) delivery path already emitted this reply to SignalR, so the
    /// unconditional sink must not emit it a second time. The sink exists because SignalR/portal is a
    /// channel-agnostic OBSERVER of every turn, not a competitor for delivery (#2631) — but observing
    /// twice is as wrong as not observing at all.
    /// </param>
    Task FanOutAsync(
        InboundMessage source,
        SessionId sessionId,
        string? content,
        ConversationId conversationId,
        CancellationToken ct,
        bool primaryDeliveredToSignalR = false);
}

/// <summary>
/// No-op <see cref="IOutboundResponseDeliverer"/> used when no conversation router is configured
/// (nothing to fan out to). Preserves the prior GatewayHost behaviour where a null router
/// short-circuited the fan-out path.
/// </summary>
internal sealed class NullOutboundResponseDeliverer : IOutboundResponseDeliverer
{
    /// <summary>Shared singleton no-op instance.</summary>
    public static readonly NullOutboundResponseDeliverer Instance = new();

    private NullOutboundResponseDeliverer() { }

    /// <inheritdoc />
    public Task FanOutAsync(
        InboundMessage source,
        SessionId sessionId,
        string? content,
        ConversationId conversationId,
        CancellationToken ct,
        bool primaryDeliveredToSignalR = false) => Task.CompletedTask;
}
