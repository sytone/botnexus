using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Agent.Providers.Core.Registry;

/// <summary>
/// Inferred capability set for a dynamic (user-defined / config-declared / discovered) model
/// (PBI6, issue #1707). Dynamic models are born from configuration or a provider's runtime
/// discovery response rather than the hand-curated built-in table, so they do not automatically
/// carry the reasoning / extra-high / extended-context flags that drive the agent- and
/// conversation-level pickers. This record is the single sanctioned home for turning a model id
/// (and any explicitly declared capability values) into the concrete capability flags that
/// <see cref="LlmModel"/> exposes, so the pickers offer only valid thinking/context choices for a
/// dynamic model exactly as they do for a built-in one.
/// </summary>
/// <param name="Reasoning">Whether the model supports a thinking/reasoning override at all.</param>
/// <param name="SupportsExtraHighThinking">Whether the model supports the ExtraHigh / Max thinking tiers.</param>
/// <param name="SupportsExtendedContextWindow">Whether the model can be driven with the extended (1M) context window.</param>
public readonly record struct DynamicModelCapabilities(
    bool Reasoning,
    bool SupportsExtraHighThinking,
    bool SupportsExtendedContextWindow)
{
    /// <summary>
    /// Infers the capability set for a dynamic model from its id, honouring any explicitly declared
    /// values first and falling back to model-family heuristics when a value is omitted
    /// (<see langword="null"/>). This is the "defaults inferred from the model family when omitted"
    /// contract from the PBI6 acceptance criteria: a config author may pin a capability precisely,
    /// but when they say nothing we derive a sensible default from the family instead of assuming a
    /// non-reasoning, standard-context model.
    /// </summary>
    /// <param name="modelId">The dynamic model id (for example <c>claude-opus-4.6</c> or <c>gpt-5.2</c>).</param>
    /// <param name="declaredReasoning">An explicit reasoning declaration, or <see langword="null"/> to infer from the family.</param>
    /// <param name="declaredExtraHighThinking">An explicit extra-high declaration, or <see langword="null"/> to infer from the family.</param>
    /// <param name="declaredExtendedContext">An explicit extended-context declaration, or <see langword="null"/> to infer from the family.</param>
    /// <returns>The resolved capability flags to stamp onto the dynamic model.</returns>
    public static DynamicModelCapabilities Infer(
        string modelId,
        bool? declaredReasoning = null,
        bool? declaredExtraHighThinking = null,
        bool? declaredExtendedContext = null)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        var reasoning = declaredReasoning ?? InferReasoning(modelId);
        // Extra-high can only be true for a reasoning model: a non-reasoning model has no thinking
        // tiers at all, so an explicit extra-high=true on a non-reasoning model is meaningless and
        // is clamped off. This keeps GetSupportedThinkingLevels internally consistent.
        var extraHigh = (declaredExtraHighThinking ?? InferExtraHighThinking(modelId)) && reasoning;
        var extendedContext = declaredExtendedContext ?? InferExtendedContext(modelId);

        return new DynamicModelCapabilities(reasoning, extraHigh, extendedContext);
    }

    /// <summary>
    /// Family heuristic for reasoning support. Recognises the Claude 4+, GPT-5+, o3/o4, Gemini 3+
    /// and Grok-code families, plus the historical <c>reasoning</c> name hint so pre-PBI6 config
    /// that relied on it keeps working.
    /// </summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>True when the family is known to support reasoning.</returns>
    public static bool InferReasoning(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        if (modelId.StartsWith("claude-opus-4", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("claude-sonnet-4", StringComparison.OrdinalIgnoreCase))
            return true;

        if (modelId.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase))
            return true;

        if (modelId.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
            return true;

        if (modelId.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase))
            return true;

        if (modelId.StartsWith("grok-code", StringComparison.OrdinalIgnoreCase))
            return true;

        // Backward-compatible name hint retained from the pre-PBI6 dynamic-registration path.
        if (modelId.Contains("reasoning", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// Family heuristic for extra-high thinking support: Claude Opus 4.6+ and GPT-5.2+ carry the
    /// top thinking tiers. Mirrors the discovery-provider heuristic so a discovered and a
    /// config-declared model of the same family agree.
    /// </summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>True when the family is known to support the ExtraHigh / Max tiers.</returns>
    public static bool InferExtraHighThinking(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        if (modelId.StartsWith("claude-opus-4.", StringComparison.OrdinalIgnoreCase))
        {
            var versionPart = modelId.AsSpan()["claude-opus-4.".Length..];
            if (versionPart.Length > 0 && char.IsDigit(versionPart[0]) && versionPart[0] >= '6')
                return true;
        }

        if (modelId.StartsWith("gpt-5.", StringComparison.OrdinalIgnoreCase))
        {
            var versionPart = modelId.AsSpan()["gpt-5.".Length..];
            if (versionPart.Length > 0 && char.IsDigit(versionPart[0]) && versionPart[0] >= '2')
                return true;
        }

        return false;
    }

    /// <summary>
    /// Family heuristic for the extended (1M) context window: the Anthropic-direct Claude Sonnet 4 /
    /// 4.5 and Opus 4.5 families advertise it. Config authors targeting an OpenAI-compatible local
    /// endpoint (Ollama, LM Studio) get the standard single-window default unless they declare
    /// otherwise.
    /// </summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>True when the family is known to support the extended context window.</returns>
    public static bool InferExtendedContext(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        // Anthropic-direct Claude Sonnet 4/4.5 and Opus 4.5 carry the 1M extended window.
        if (modelId.StartsWith("claude-sonnet-4", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("claude-opus-4-5", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
