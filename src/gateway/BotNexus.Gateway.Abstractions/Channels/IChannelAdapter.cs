using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Channels;

/// <summary>
/// Adapter for an external communication channel (Telegram, Discord, Slack, TUI, etc.).
/// Channel adapters are pluggable — they receive messages from external sources and
/// forward them to the Gateway via the message routing pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Channel adapters run independently of the Gateway API surface. The API's native
/// WebSocket endpoint is <b>not</b> a channel adapter — it's built into Gateway.Api.
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
    string ChannelType { get; }

    /// <summary>Human-readable display name for this channel.</summary>
    string DisplayName { get; }

    /// <summary>Whether this channel supports streaming (deltas) vs. complete messages only.</summary>
    bool SupportsStreaming { get; }

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
    /// <param name="conversationId">The target conversation.</param>
    /// <param name="delta">The incremental content.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SendStreamDeltaAsync(string conversationId, string delta, CancellationToken cancellationToken = default);
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
