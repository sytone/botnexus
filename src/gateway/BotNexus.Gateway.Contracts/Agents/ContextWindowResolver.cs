using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// The single sanctioned derivation of "how large is this run's context window", used by the
/// diagnostics surfaces that report context headroom (#3091).
/// </summary>
/// <remarks>
/// <para>
/// Before #3091 the context endpoint reported a compile-time <c>128000</c> for every agent on every
/// model, and derived <c>usagePercent</c> from it - so headroom was over-reported against a 200K
/// model and under-reported roughly fourfold against a 32K one, silently and with no error.
/// </para>
/// <para>
/// The deliberate design point is that this returns <see langword="null"/> rather than a
/// plausible-looking default when the window genuinely cannot be established. A wrong number that
/// looks right is worse than an absent one: a consumer computing headroom cannot detect the former
/// and can detect the latter. Do <strong>not</strong> add a fallback literal here.
/// </para>
/// </remarks>
public static class ContextWindowResolver
{
    /// <summary>
    /// Resolves the effective context window in tokens, or <see langword="null"/> when it cannot be
    /// established from the supplied inputs.
    /// </summary>
    /// <param name="effectiveOverride">
    /// The context window selected by the conversation &gt; agent override stack (see
    /// <c>ModelOverrideResolver</c>), or <see langword="null"/> when no layer selected one. Most
    /// specific, so it wins over the registered model's default.
    /// </param>
    /// <param name="model">
    /// The registered model the run is bound to, or <see langword="null"/> when the model could not
    /// be resolved from the registry.
    /// </param>
    /// <returns>The resolved window in tokens, or <see langword="null"/> when unresolvable.</returns>
    public static int? Resolve(int? effectiveOverride, LlmModel? model)
        => ResolveBudget(effectiveOverride, overrideSource: null, new ContextBudgetDiagnostics
        {
            ModelContextWindowTokens = model?.ContextWindow,
            ModelContextWindowSource = model?.ContextWindowSource,
            ModelMaxOutputTokens = model?.MaxTokens,
            ModelMaxOutputSource = model?.MaxTokensSource
        }).EffectiveWorkingBudgetTokens;

    /// <summary>
    /// Resolves the same working window together with its selection origin and independent model
    /// declarations. The caller supplies the origin of the already-selected conversation/agent
    /// override. No clamping or output reserve is applied, including for extended-context overrides.
    /// </summary>
    /// <param name="effectiveOverride">The override selected by the existing precedence stack.</param>
    /// <param name="overrideSource">Conversation or agent; null when the caller does not know.</param>
    /// <param name="modelDeclarations">
    /// The registered model's declarations projected into the gateway contract, or null when
    /// unresolvable. Only model declaration fields are read; any working budget fields are ignored.
    /// </param>
    /// <returns>A source-backed snapshot; unknown values and their origins remain null.</returns>
    public static ContextBudgetDiagnostics ResolveBudget(
        int? effectiveOverride, string? overrideSource, ContextBudgetDiagnostics? modelDeclarations)
    {
        var modelWindow = modelDeclarations?.ModelContextWindowTokens is > 0
            ? modelDeclarations.ModelContextWindowTokens : null;
        var modelOutput = modelDeclarations?.ModelMaxOutputTokens is > 0
            ? modelDeclarations.ModelMaxOutputTokens : null;
        var hasOverride = effectiveOverride is > 0;
        return new ContextBudgetDiagnostics
        {
            ModelContextWindowTokens = modelWindow,
            ModelContextWindowSource = modelWindow.HasValue ? modelDeclarations?.ModelContextWindowSource : null,
            ModelMaxOutputTokens = modelOutput,
            ModelMaxOutputSource = modelOutput.HasValue ? modelDeclarations?.ModelMaxOutputSource : null,
            EffectiveWorkingBudgetTokens = hasOverride ? effectiveOverride : modelWindow,
            EffectiveWorkingBudgetSource = hasOverride ? overrideSource : modelWindow.HasValue ? "model" : null
        };
    }
}
