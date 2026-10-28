using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Gateway.Abstractions.Agents;

namespace BotNexus.Gateway.Isolation;

/// <summary>
/// Projects provider registry declarations and selected reasoning effort into gateway-owned
/// snapshots. The registry remains the catalogue owner; this layer never resolves defaults or
/// changes a selection based on provider capabilities.
/// </summary>
internal static class ProviderModelMapping
{
    /// <summary>Copies independent declarations, leaving usability checks to ResolveBudget.</summary>
    public static ContextBudgetDiagnostics? ToContextDeclarations(this LlmModel? model) => model is null
        ? null
        : new ContextBudgetDiagnostics
        {
            ModelContextWindowTokens = model.ContextWindow,
            ModelContextWindowSource = model.ContextWindowSource,
            ModelMaxOutputTokens = model.MaxTokens,
            ModelMaxOutputSource = model.MaxTokensSource
        };

    /// <summary>
    /// Preserves the selected level, including null and undefined underlying values. Undefined
    /// values must not silently become a known level; ThinkingWireToken still reports null for them.
    /// </summary>
    public static ExecutionThinkingLevel? ToGatewayThinking(this ThinkingLevel? thinking)
        => thinking.HasValue ? (ExecutionThinkingLevel)(int)thinking.Value : null;

    /// <summary>Reverses the projection without defaulting or interpreting provider support.</summary>
    public static ThinkingLevel? ToProviderThinking(this ExecutionThinkingLevel? thinking)
        => thinking.HasValue ? (ThinkingLevel)(int)thinking.Value : null;
}
