using BotNexus.Agent.Core.ExtensionPoints.ToolResults;

namespace BotNexus.Agent.Core.ExtensionPoints.Messages;

/// <summary>Request-only facts for rendering dynamic context, with no mutable transcript access.</summary>
/// <param name="ToolCapacity">The existing guard's current accounting for this request.</param>
public sealed record ProviderRequestContext(ToolCapacitySnapshot ToolCapacity);
