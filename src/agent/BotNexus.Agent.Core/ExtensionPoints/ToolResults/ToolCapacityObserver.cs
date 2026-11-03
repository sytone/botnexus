namespace BotNexus.Agent.Core.ExtensionPoints.ToolResults;

/// <summary>
/// Reports run start and the 75%, 90% and exhaustion crossings without control authority.
/// Multicast subscribers run synchronously in registration order with independent exception isolation,
/// including subscriber cancellation. Callbacks must return promptly; no timeout is imposed.
/// Operation cancellation remains owned by the loop, not this sink.
/// </summary>
/// <param name="observation">Immutable scalar run accounting; no transcript or tool content.</param>
public delegate void ToolCapacityObserver(ToolCapacityObservation observation);
