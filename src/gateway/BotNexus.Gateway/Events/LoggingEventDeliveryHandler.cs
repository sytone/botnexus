// LoggingEventDeliveryHandler.cs
using BotNexus.Gateway.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace BotNexus.Gateway.Events;

/// <summary>
/// Default delivery handler that logs the event delivery. In production this would
/// create a session on the target agent, but the session-dispatch wiring is deferred
/// to a follow-up issue. This allows the bus infrastructure to be tested independently.
/// </summary>
public sealed class LoggingEventDeliveryHandler : IEventDeliveryHandler
{
    private readonly ILogger<LoggingEventDeliveryHandler> _logger;

    /// <summary>Creates a new logging delivery handler.</summary>
    public LoggingEventDeliveryHandler(ILogger<LoggingEventDeliveryHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task DeliverAsync(string agentId, WorldEvent worldEvent, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Event delivery: {EventType} → agent {AgentId} (source: {SourceAgentId}, payload keys: {PayloadKeys})",
            worldEvent.EventType,
            agentId,
            worldEvent.SourceAgentId ?? "system",
            string.Join(", ", worldEvent.Payload.Keys));

        return Task.CompletedTask;
    }
}
