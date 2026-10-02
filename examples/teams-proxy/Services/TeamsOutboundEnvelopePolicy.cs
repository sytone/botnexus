using BotNexus.TeamsProxy.Models;

namespace BotNexus.TeamsProxy.Services;

/// <summary>
/// Defines the Teams activity boundary for Service Bus reply envelopes.
/// </summary>
public static class TeamsOutboundEnvelopePolicy
{
    /// <summary>
    /// Returns <see langword="true"/> only for a non-empty consolidated terminal response.
    /// Raw deltas are transport chunks and must never become standalone Teams activities.
    /// </summary>
    public static bool ShouldPostActivity(ServiceBusOutboundEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        return envelope.IsFinal
            && string.Equals(envelope.Type, "done", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(envelope.Content);
    }
}
