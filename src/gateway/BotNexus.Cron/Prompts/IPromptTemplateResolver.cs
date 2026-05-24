using BotNexus.Domain.Primitives;

namespace BotNexus.Cron.Prompts;

/// <summary>
/// Resolves named prompt templates for runtime execution.
/// </summary>
public interface IPromptTemplateResolver
{
    /// <summary>
    /// Lists discovered template names available to the specified agent.
    /// </summary>
    /// <param name="agentId">Agent identifier used for per-agent and workspace template discovery.</param>
    /// <returns>Sorted template names.</returns>
    IReadOnlyList<string> ListTemplateNames(AgentId agentId);

    /// <summary>
    /// Renders a named template for the specified agent.
    /// </summary>
    /// <param name="agentId">Agent identifier used for per-agent and workspace template discovery.</param>
    /// <param name="templateName">Template name to resolve and render.</param>
    /// <param name="parameters">Optional caller-provided parameter values.</param>
    /// <param name="renderedPrompt">Rendered prompt when successful.</param>
    /// <param name="error">Deterministic error message when render fails.</param>
    /// <returns><c>true</c> when the template was rendered successfully; otherwise <c>false</c>.</returns>
    bool TryRender(
        AgentId agentId,
        string templateName,
        IReadOnlyDictionary<string, string?>? parameters,
        out string renderedPrompt,
        out string? error);
}
