using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Resolution;

/// <summary>
/// One layer of the three-layer model/thinking/context override stack. Each field is
/// optional (<see langword="null"/> means "unset - fall through to the next layer"). The
/// three layers, from least to most specific, are: model defaults, agent configuration,
/// and conversation override.
/// </summary>
/// <param name="Model">The model identifier this layer selects, or <see langword="null"/> when unset.</param>
/// <param name="Thinking">The thinking level this layer selects, or <see langword="null"/> when unset.</param>
/// <param name="ContextWindow">The context-window size this layer selects, or <see langword="null"/> when unset.</param>
public readonly record struct ModelOverrideLayer(
    string? Model = null,
    ThinkingLevel? Thinking = null,
    int? ContextWindow = null);

/// <summary>
/// The effective, fully-resolved model selection produced by <see cref="ModelOverrideResolver"/>.
/// <see cref="Thinking"/> and <see cref="ContextWindow"/> remain <see langword="null"/> when no
/// layer selected them, signalling "use the provider default".
/// </summary>
/// <param name="Model">
/// The resolved model identifier. Named <c>Model</c> (not <c>ModelId</c>) so the resolver output
/// is not mistaken for a raw descriptor read by the direct-resolution architecture fence.
/// </param>
/// <param name="Thinking">The resolved thinking level, or <see langword="null"/> for the provider default.</param>
/// <param name="ContextWindow">The resolved context-window size, or <see langword="null"/> for the provider default.</param>
public readonly record struct EffectiveModelResolution(
    string? Model,
    ThinkingLevel? Thinking,
    int? ContextWindow);

/// <summary>
/// Centralized, pure resolver for the epic's three-layer override precedence
/// (model defaults -&gt; agent config -&gt; conversation override). This is the single
/// sanctioned home for turning the layered configuration into an effective
/// {model, thinking, context} selection; every spawn / cron / isolation / supervisor
/// path routes through it instead of reading <c>ModelId</c> ad hoc.
/// </summary>
/// <remarks>
/// Each field is resolved independently and most-specific-wins: the conversation layer
/// beats the agent layer, which beats the model-defaults layer. An unset field
/// (<see langword="null"/>) falls through to the next layer, so a conversation that
/// overrides only the thinking level still inherits the agent's model and context.
/// The function is pure (no I/O, no statics touched) so the precedence is trivially
/// unit-testable field by field.
/// </remarks>
public static class ModelOverrideResolver
{
    /// <summary>
    /// Resolves the effective model selection from the three override layers using
    /// most-specific-wins precedence, resolved independently per field.
    /// </summary>
    /// <param name="modelDefaults">The least-specific layer: per-model defaults.</param>
    /// <param name="agent">The middle layer: agent-level configuration.</param>
    /// <param name="conversation">The most-specific layer: per-conversation override.</param>
    /// <returns>The effective, fully-resolved selection.</returns>
    public static EffectiveModelResolution Resolve(
        ModelOverrideLayer modelDefaults,
        ModelOverrideLayer agent,
        ModelOverrideLayer conversation)
    {
        return new EffectiveModelResolution(
            Model: conversation.Model ?? agent.Model ?? modelDefaults.Model,
            Thinking: conversation.Thinking ?? agent.Thinking ?? modelDefaults.Thinking,
            ContextWindow: conversation.ContextWindow ?? agent.ContextWindow ?? modelDefaults.ContextWindow);
    }
}
