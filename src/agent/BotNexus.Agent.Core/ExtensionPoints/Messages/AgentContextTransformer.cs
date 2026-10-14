using BotNexus.Agent.Core.Types;

namespace BotNexus.Agent.Core.ExtensionPoints.Messages;

/// <summary>
/// Transforms agent context messages before provider invocation.
/// </summary>
/// <param name="messages">The source message list.</param>
/// <param name="cancellationToken">The cancellation token.</param>
/// <returns>A transformed message list.</returns>
/// <remarks>
/// Use to filter, summarize, or rewrite messages before they reach the LLM.
/// Contract: must not throw. Return the original list or a safe fallback.
/// </remarks>
public delegate Task<IReadOnlyList<AgentMessage>> AgentContextTransformer(
    IReadOnlyList<AgentMessage> messages,
    CancellationToken cancellationToken);