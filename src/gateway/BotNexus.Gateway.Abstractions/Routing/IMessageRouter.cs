using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Routing;

/// <summary>
/// Routes inbound messages to the appropriate agent(s).
/// The router examines the message metadata, explicit targets, and configured defaults
/// to determine which agent(s) should handle a message.
/// </summary>
/// <remarks>
/// <para>Routing priority (highest to lowest):</para>
/// <list type="number">
///   <item>Explicit <see cref="InboundMessage.TargetAgentId"/> — message is sent to that agent.</item>
///   <item>Session-bound agent — if <see cref="InboundMessage.SessionId"/> is set and the session
///   has an existing agent binding, that agent is used.</item>
///   <item>Channel-specific routing rules — configured per channel type.</item>
///   <item>Default agent — the Gateway's configured default agent.</item>
/// </list>
/// </remarks>
public interface IMessageRouter
{
    /// <summary>
    /// Resolves which agent IDs should receive an inbound message.
    /// Returns one or more agent IDs. An empty result means the message is unroutable.
    /// </summary>
    /// <param name="message">The inbound message to route.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The agent IDs that should handle this message.</returns>
    Task<IReadOnlyList<string>> ResolveAsync(InboundMessage message, CancellationToken cancellationToken = default);
}
