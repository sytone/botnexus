using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// Composes the final system prompt for an agent from descriptor and workspace context.
/// </summary>
public interface IContextBuilder
{
    /// <summary>
    /// Builds the effective system prompt for an agent.
    /// </summary>
    /// <param name="descriptor">The agent descriptor.</param>
    /// <param name="executionContext">Runtime session context used for conversation-scoped prompt data.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The composed system prompt text.</returns>
    Task<string> BuildSystemPromptAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext? executionContext,
        CancellationToken cancellationToken = default);
}
