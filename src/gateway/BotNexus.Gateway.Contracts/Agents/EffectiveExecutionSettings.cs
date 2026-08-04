using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// The fully-resolved execution settings for one agent handle: the model, provider, thinking
/// level, and context window that the run will actually use after model-default, agent, and
/// conversation precedence has been applied.
/// </summary>
/// <remarks>
/// <para>
/// This type exists because of issue #2796. Before it, the effective settings had <em>two</em>
/// independent derivations: <c>InProcessIsolationStrategy</c> resolved them through
/// <c>ModelOverrideResolver</c> for the registered model and <c>AgentOptions</c>, while
/// <c>WorkspaceContextBuilder</c> separately read <c>descriptor.ApiProvider</c> /
/// <c>descriptor.ModelId</c> for the injected runtime block. The two silently drifted: a
/// conversation whose persisted model override was honoured at execution time still told the
/// agent - and therefore the user - that it was running the agent descriptor default.
/// </para>
/// <para>
/// The fix is to compute the settings once and thread <em>this single value</em> from the place
/// they are resolved into prompt / runtime-context construction. Do <strong>not</strong> re-resolve
/// the override inside the context builder, and do <strong>not</strong> read the descriptor there
/// for these four fields: either would reintroduce a second (or third) spelling of "what model is
/// this run using" and re-open #2796. The descriptor's configured model survives only as
/// <see cref="DescriptorDefaultModel"/>, an explicitly labelled default.
/// </para>
/// </remarks>
/// <param name="Provider">The API provider the run is bound to.</param>
/// <param name="Model">The effective model id after conversation &gt; agent &gt; model-default resolution.</param>
/// <param name="DescriptorDefaultModel">
/// The agent descriptor's configured model. Carried so a surface can label the default when it
/// differs from <paramref name="Model"/>; it is never the value reported as the run's model.
/// </param>
/// <param name="Thinking">
/// The effective thinking level, or <see langword="null"/> when no layer selected one and the
/// provider default applies.
/// </param>
/// <param name="ContextWindow">
/// The effective context-window size, or <see langword="null"/> when no layer selected one and the
/// provider default applies.
/// </param>
public sealed record EffectiveExecutionSettings(
    string? Provider,
    string? Model,
    string? DescriptorDefaultModel,
    ThinkingLevel? Thinking,
    int? ContextWindow)
{
    /// <summary>
    /// Renders <see cref="Thinking"/> as the wire token the runtime block and status surfaces use
    /// ("minimal", "low", "medium", "high", "xhigh", "max"), or <see langword="null"/> when no
    /// level is set so the caller can apply its own "off" wording.
    /// </summary>
    public string? ThinkingWireToken => Thinking switch
    {
        ThinkingLevel.Minimal => "minimal",
        ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        ThinkingLevel.ExtraHigh => "xhigh",
        ThinkingLevel.Max => "max",
        _ => null
    };

    /// <summary>
    /// The descriptor default, but only when it actually differs from the effective
    /// <see cref="Model"/>. Returns <see langword="null"/> when no override is in play so an
    /// un-overridden session's runtime block stays byte-identical to its pre-#2796 form.
    /// </summary>
    public string? DivergentDescriptorDefaultModel =>
        !string.IsNullOrWhiteSpace(DescriptorDefaultModel)
        && !string.Equals(DescriptorDefaultModel, Model, StringComparison.OrdinalIgnoreCase)
            ? DescriptorDefaultModel
            : null;
}
