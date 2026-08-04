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
    /// <param name="effectiveSettings">
    /// The already-resolved execution settings for this run (issue #2796). Callers that have
    /// resolved the three-layer model/thinking/context override MUST pass it here so the injected
    /// runtime block reports the same values the handle actually executes with. Implementations
    /// must not re-resolve the override or read the descriptor for these fields when a value is
    /// supplied - doing so recreates the second, stale derivation that caused #2796.
    /// <see langword="null"/> is reserved for callers with no run context at all, which then
    /// legitimately render the descriptor defaults.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The composed system prompt text.</returns>
    Task<string> BuildSystemPromptAsync(
        AgentDescriptor descriptor,
        AgentExecutionContext? executionContext,
        EffectiveExecutionSettings? effectiveSettings = null,
        CancellationToken cancellationToken = default);
}
