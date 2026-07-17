using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Domain.Primitives;

namespace BotNexus.Gateway.Abstractions.Channels;

/// <summary>
/// Adapter for an external communication channel (Telegram, Discord, Slack, TUI, etc.).
/// Channel adapters are pluggable — they receive messages from external sources and
/// forward them to the Gateway via the message routing pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Channel adapters run independently of the Gateway API surface. The API's native
/// SignalR hub endpoint is <b>not</b> a channel adapter — it's built into Gateway.Api.
/// Channel adapters are for external protocols that need their own connection management.
/// </para>
/// <para>
/// Adapters are started/stopped by the Gateway host and registered via DI.
/// They publish inbound messages and consume outbound messages through the
/// <see cref="IChannelDispatcher"/> callback.
/// </para>
/// </remarks>
public interface IChannelAdapter
{
    /// <summary>The channel type identifier (e.g., "telegram", "discord", "tui").</summary>
    ChannelKey ChannelType { get; }

    /// <summary>
    /// Optional adapter instance identifier used to distinguish multiple adapters of the same
    /// <see cref="ChannelType"/> (e.g., two Telegram bots). When set, <see cref="IChannelManager"/>
    /// can select the correct adapter for a given binding.
    /// Returns <c>null</c> for single-instance channel types.
    /// </summary>
    string? AdapterId => null;

    /// <summary>Human-readable display name for this channel.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this channel can deliver streaming responses. The gateway combines this capability
    /// with <see cref="InboundMessage.StreamResponse"/>: a message may opt out, while a capable
    /// channel can remain opt-in by returning <c>false</c> here and implementing
    /// <see cref="IStreamEventChannelAdapter"/>.
    /// </summary>
    bool SupportsStreaming { get; }

    /// <summary>Whether this channel supports real-time steering inputs during a running response.</summary>
    bool SupportsSteering { get; }

    /// <summary>Whether this channel supports follow-up message controls on an existing response.</summary>
    bool SupportsFollowUp { get; }

    /// <summary>Whether this channel can render model thinking/progress output.</summary>
    bool SupportsThinkingDisplay { get; }

    /// <summary>Whether this channel can render tool call activity output.</summary>
    bool SupportsToolDisplay { get; }

    /// <summary>
    /// Whether this channel can receive inbound image attachments from users.
    /// When true, the adapter populates <see cref="BotNexus.Gateway.Abstractions.Models.InboundMessage.ContentParts"/>
    /// with image binary data for vision-capable models.
    /// </summary>
    bool SupportsInboundImages { get; }

    /// <summary>Whether the adapter is currently running and accepting messages.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Starts the channel adapter, beginning to listen for inbound messages.
    /// </summary>
    /// <param name="dispatcher">
    /// Callback for dispatching inbound messages into the Gateway routing pipeline.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for shutdown.</param>
    Task StartAsync(IChannelDispatcher dispatcher, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the channel adapter and disconnects from the external service.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a complete message to a conversation on this channel.
    /// </summary>
    /// <param name="message">The outbound message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendAsync(OutboundMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a streaming delta to a conversation on this channel.
    /// Only called if <see cref="SupportsStreaming"/> is <c>true</c>.
    /// </summary>
    /// <param name="target">
    /// Typed routing target identifying the session, channel address, and originating
    /// binding for this stream. Each adapter consumes the field that matches its
    /// routing semantics — see <see cref="ChannelStreamTarget"/> for the per-channel
    /// contract.
    /// </param>
    /// <param name="delta">The incremental content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendStreamDeltaAsync(ChannelStreamTarget target, string delta, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional contract for forwarding adapters whose stable outbound destination differs from
/// their inbound channel identity. The gateway uses this when de-duplicating primary delivery
/// against observer fan-out to the same adapter destination.
/// </summary>
public interface IChannelDestinationResolver
{
    /// <summary>
    /// Resolves the adapter that ultimately receives stream events for the session, or
    /// <c>null</c> when no deliverable destination is available.
    /// </summary>
    /// <param name="sessionId">Session whose persisted channel determines the destination.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IChannelAdapter?> ResolveStreamDestinationAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Callback interface for channel adapters to dispatch inbound messages
/// into the Gateway routing pipeline.
/// </summary>
public interface IChannelDispatcher
{
    /// <summary>
    /// Dispatches an inbound message for routing and agent processing.
    /// </summary>
    /// <param name="message">The inbound message from the channel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional contract for channels that can render structured streaming events.
/// </summary>
public interface IStreamEventChannelAdapter
{
    /// <summary>
    /// Sends a structured stream event to a target conversation.
    /// </summary>
    /// <param name="target">
    /// Typed routing target identifying the session, channel address, and originating
    /// binding for this stream. See <see cref="ChannelStreamTarget"/> for the
    /// per-channel contract.
    /// </param>
    /// <param name="streamEvent">The stream event to deliver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendStreamEventAsync(ChannelStreamTarget target, AgentStreamEvent streamEvent, CancellationToken cancellationToken = default);
}
