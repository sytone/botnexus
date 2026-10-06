using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.ExtensionPoints.Messages;

/// <summary>
/// Produces contextual message lists such as steering or follow-up messages.
/// </summary>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>The produced message list.</returns>
/// <remarks>
/// Called at turn boundaries (steering) or run completion (follow-up).
/// Return an empty list if no messages are available. Must not throw.
/// </remarks>
public delegate Task<IReadOnlyList<AgentMessage>> AgentMessageProvider(CancellationToken cancellationToken);