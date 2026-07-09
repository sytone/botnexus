using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Resolution;

/// <summary>
/// The outcome of validating a requested per-conversation override against a concrete model's
/// capabilities. <see cref="IsValid"/> is <see langword="true"/> when the request is acceptable;
/// otherwise <see cref="Error"/> carries a human-readable reason suitable for a 400 response.
/// </summary>
/// <param name="IsValid">Whether the requested override is acceptable for the model.</param>
/// <param name="Error">The rejection reason when <see cref="IsValid"/> is <see langword="false"/>; otherwise <see langword="null"/>.</param>
public readonly record struct OverrideValidationResult(bool IsValid, string? Error)
{
    /// <summary>Gets a successful validation result.</summary>
    public static OverrideValidationResult Ok { get; } = new(true, null);

    /// <summary>Builds a failed validation result carrying the supplied rejection reason.</summary>
    /// <param name="error">The human-readable rejection reason.</param>
    /// <returns>A failed <see cref="OverrideValidationResult"/>.</returns>
    public static OverrideValidationResult Invalid(string error) => new(false, error);
}

/// <summary>
/// Pure capability validator for per-conversation model / thinking / context overrides (PBI5,
/// issue #1706). Given the concrete <see cref="LlmModel"/> that a requested override would run
/// against, it decides whether a requested thinking level or context-window size is expressible by
/// that model. This is the single sanctioned home for "is this override valid for this model" so
/// the API boundary and any future callers share one rule set instead of re-deriving it.
/// </summary>
/// <remarks>
/// The function is pure (no I/O, no statics touched) so each rule is trivially unit-testable. The
/// model-id itself is not validated here - whether a model id is registered/allowed for an agent is
/// a registry concern the caller resolves before calling this validator; by the time we are here the
/// <paramref name="model"/> has already been resolved from the registry.
/// </remarks>
public static class ConversationOverrideValidator
{
    /// <summary>
    /// Validates a requested thinking level against the model's reasoning capabilities.
    /// A model that does not support reasoning rejects any thinking override; the two top tiers
    /// (<see cref="ThinkingLevel.ExtraHigh"/> and <see cref="ThinkingLevel.Max"/>) additionally
    /// require <see cref="LlmModel.SupportsExtraHighThinking"/>.
    /// </summary>
    /// <param name="model">The model the override would run against.</param>
    /// <param name="thinking">The requested thinking level.</param>
    /// <returns>The validation outcome.</returns>
    public static OverrideValidationResult ValidateThinking(LlmModel model, ThinkingLevel thinking)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (!model.Reasoning)
            return OverrideValidationResult.Invalid(
                $"Model '{model.Id}' does not support a thinking/reasoning override.");

        if ((thinking is ThinkingLevel.ExtraHigh or ThinkingLevel.Max) && !model.SupportsExtraHighThinking)
            return OverrideValidationResult.Invalid(
                $"Model '{model.Id}' does not support the '{thinking}' thinking level.");

        return OverrideValidationResult.Ok;
    }

    /// <summary>
    /// Validates a requested context-window size (in tokens) against the model's maximum. The
    /// value must be positive and must not exceed <see cref="LlmModel.ContextWindow"/>. Shrinking
    /// the window below the model maximum is always allowed (a conversation may deliberately cap
    /// context to save cost); only over-provisioning past what the model can address is rejected.
    /// </summary>
    /// <param name="model">The model the override would run against.</param>
    /// <param name="contextWindow">The requested context-window size in tokens.</param>
    /// <returns>The validation outcome.</returns>
    public static OverrideValidationResult ValidateContextWindow(LlmModel model, int contextWindow)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (contextWindow <= 0)
            return OverrideValidationResult.Invalid("Context window override must be a positive number of tokens.");

        if (contextWindow > model.ContextWindow)
            return OverrideValidationResult.Invalid(
                $"Context window override {contextWindow} exceeds the maximum {model.ContextWindow} for model '{model.Id}'.");

        return OverrideValidationResult.Ok;
    }
}
