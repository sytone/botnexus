using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BotNexus.Gateway.Abstractions.Notifications;

/// <summary>
/// Raises a notification from somewhere in the gateway.
/// </summary>
/// <remarks>
/// Strictly advisory, on the same terms as <c>IAgentExchangeProgressNotifier</c>: an implementation
/// must never throw into the path that raised it. A notification is a report ABOUT work, and
/// failing to file the report must never be able to break the work itself - a gateway that could
/// not start because it could not tell you it had not started would be worse than useless.
/// </remarks>
public interface INotificationPublisher
{
    /// <summary>Raises a notification. Failures are contained, never propagated.</summary>
    /// <param name="notification">What to raise.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PublishAsync(Notification notification, CancellationToken ct = default);
}

/// <summary>
/// Raising helpers that make the "never throws" guarantee structural.
/// </summary>
public static class NotificationPublisherExtensions
{
    /// <summary>
    /// Raises a notification, containing any failure including one thrown by the publisher itself.
    /// </summary>
    /// <remarks>
    /// The interface documents that implementations must not throw, but a caller that DEPENDS on
    /// that is trusting every present and future implementation to honour it. Since the entire
    /// point is that reporting work must never break the work, the guarantee belongs here - once -
    /// rather than in a try/catch every call site has to remember to write.
    /// </remarks>
    /// <param name="publisher">Publisher to raise through.</param>
    /// <param name="notification">What to raise.</param>
    /// <param name="logger">Optional logger for a contained failure.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task TryPublishAsync(
        this INotificationPublisher publisher,
        Notification notification,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        if (publisher is null || notification is null)
            return;

        try
        {
            await publisher.PublishAsync(notification, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(
                ex,
                "Could not raise the {Kind} notification '{Title}'.",
                notification.Kind,
                notification.Title);
        }
    }
}

/// <summary>
/// No-op publisher. The DI fallback and the default for the many direct-construction call sites, so
/// a caller never has to null-check before raising.
/// </summary>
public sealed class NullNotificationPublisher : INotificationPublisher
{
    /// <summary>Shared singleton no-op instance.</summary>
    public static readonly NullNotificationPublisher Instance = new();

    private NullNotificationPublisher() { }

    /// <inheritdoc />
    public Task PublishAsync(Notification notification, CancellationToken ct = default) => Task.CompletedTask;
}
