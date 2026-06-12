// WorldEvent.cs
namespace BotNexus.Gateway.Contracts.Events;

/// <summary>
/// A lightweight event fired within the gateway that agents can subscribe to.
/// Events carry a typed payload and routing metadata for subscription matching.
/// </summary>
/// <param name="EventType">The event type identifier (e.g. "agent.error", "cron.failed").</param>
/// <param name="Payload">Arbitrary payload data as a dictionary of key-value pairs.</param>
/// <param name="SourceAgentId">The agent that produced the event, if applicable.</param>
/// <param name="Timestamp">When the event was fired.</param>
public sealed record WorldEvent(
    string EventType,
    IReadOnlyDictionary<string, string> Payload,
    string? SourceAgentId,
    DateTimeOffset Timestamp)
{
    /// <summary>Creates a new event with the current UTC timestamp.</summary>
    public static WorldEvent Create(string eventType, IReadOnlyDictionary<string, string>? payload = null, string? sourceAgentId = null) =>
        new(eventType, payload ?? new Dictionary<string, string>(), sourceAgentId, DateTimeOffset.UtcNow);
}

/// <summary>
/// Known event types in the world event bus.
/// </summary>
public static class WorldEventTypes
{
    /// <summary>A new agent was registered.</summary>
    public const string AgentRegistered = "agent.registered";

    /// <summary>An agent encountered repeated failures.</summary>
    public const string AgentError = "agent.error";

    /// <summary>A cron job failed.</summary>
    public const string CronFailed = "cron.failed";

    /// <summary>A platform health check failed.</summary>
    public const string HealthDegraded = "health.degraded";

    /// <summary>An agent explicitly escalated to another.</summary>
    public const string ConversationEscalated = "conversation.escalated";

    /// <summary>A new agent creation/modification proposal is awaiting review.</summary>
    public const string ProposalPending = "proposal.pending";
}
