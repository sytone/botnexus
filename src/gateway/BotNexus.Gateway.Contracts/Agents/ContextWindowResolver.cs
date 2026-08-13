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
    {
        if (effectiveOverride is > 0)
            return effectiveOverride;

        // A registered model carrying a non-positive window declares nothing usable; treating it as
        // a real window would emit a divide-by-zero usage percentage or a nonsense headroom.
        return model?.ContextWindow is > 0 ? model.ContextWindow : null;
    }
}
