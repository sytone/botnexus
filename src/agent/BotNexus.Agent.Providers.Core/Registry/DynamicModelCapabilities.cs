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
/// <param name="Input">
/// The declared input modalities to stamp onto the model (for example <c>["text","image"]</c>).
/// Never empty - a model that accepts nothing at all is not a model.
/// </param>
public readonly record struct DynamicModelCapabilities(
    bool Reasoning,
    bool SupportsExtraHighThinking,
    bool SupportsExtendedContextWindow,
    IReadOnlyList<string> Input)
{
    /// <summary>The text input modality token.</summary>
    public const string TextModality = "text";

    /// <summary>The image (vision) input modality token.</summary>
    public const string ImageModality = "image";

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
    /// <param name="declaredInput">
    /// An explicit input-modality declaration from config, or <see langword="null"/>/empty to infer
    /// from the family. An explicit declaration always wins - a config author who says
    /// <c>["text"]</c> for a vision family gets exactly that (#2485).
    /// </param>
    /// <returns>The resolved capability flags to stamp onto the dynamic model.</returns>
    public static DynamicModelCapabilities Infer(
        string modelId,
        bool? declaredReasoning = null,
        bool? declaredExtraHighThinking = null,
        bool? declaredExtendedContext = null,
        IReadOnlyList<string>? declaredInput = null)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        var reasoning = declaredReasoning ?? InferReasoning(modelId);
        // Extra-high can only be true for a reasoning model: a non-reasoning model has no thinking
        // tiers at all, so an explicit extra-high=true on a non-reasoning model is meaningless and
        // is clamped off. This keeps GetSupportedThinkingLevels internally consistent.
        var extraHigh = (declaredExtraHighThinking ?? InferExtraHighThinking(modelId)) && reasoning;
        var extendedContext = declaredExtendedContext ?? InferExtendedContext(modelId);

        var input = declaredInput is { Count: > 0 }
            ? NormaliseModalities(declaredInput)
            : InferInputModalities(modelId);

        return new DynamicModelCapabilities(reasoning, extraHigh, extendedContext, input);
    }

    /// <summary>
    /// Family heuristic for input modalities. Config-declared models were previously hardcoded to
    /// <c>["text"]</c> at the gateway composition root, so a vision-capable model reached through a
    /// config-declared provider silently lost every image part it was handed (#2485). Recognised
    /// vision families get <c>["text","image"]</c>; everything else keeps the text-only default.
    /// </summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>The inferred input modality list; always contains <c>text</c>.</returns>
    public static IReadOnlyList<string> InferInputModalities(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        return SupportsVision(modelId)
            ? [TextModality, ImageModality]
            : [TextModality];
    }

    /// <summary>
    /// Family heuristic for vision (image input) support. Kept deliberately conservative: only
    /// families that are known to accept image parts are widened, because widening a model that
    /// cannot actually see would turn a reported drop into a provider-side request rejection.
    /// </summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>True when the family is known to accept image input.</returns>
    public static bool SupportsVision(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        // Explicit "-vision" / "vision-" name hints first - a local Ollama/LM Studio tag such as
        // "llava:13b" or "qwen2.5-vl" is the common config-declared case.
        string[] visionHints =
        [
            "vision", "llava", "-vl", "vl-", "bakllava", "moondream", "pixtral", "internvl", "minicpm-v"
        ];
        foreach (var hint in visionHints)
        {
            if (modelId.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Known multimodal hosted families.
        string[] visionFamilies = ["claude-", "gpt-4o", "gpt-4.1", "gpt-5", "gemini-", "grok-2-image", "o3", "o4-mini"];
        foreach (var family in visionFamilies)
        {
            if (modelId.StartsWith(family, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Trims, lowercases and de-duplicates an explicitly declared modality list, guaranteeing that
    /// <c>text</c> is present so no code path ever sees a model that accepts nothing.
    /// </summary>
    private static IReadOnlyList<string> NormaliseModalities(IReadOnlyList<string> declared)
    {
        var result = new List<string>(declared.Count + 1);
        foreach (var raw in declared)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var token = raw.Trim().ToLowerInvariant();
            if (!result.Contains(token, StringComparer.Ordinal))
                result.Add(token);
        }

        if (result.Count == 0)
            result.Add(TextModality);
        else if (!result.Contains(TextModality, StringComparer.Ordinal))
            result.Insert(0, TextModality);

        return result;
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

        // #2374: family+version gating lives in one shared place now.
        if (ModelCapabilityHeuristics.IsReasoningModel(modelId))
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

        // #2374: shared numeric comparison; previously a character-wise prefix parse that could not
        // classify claude-opus-5 and mis-ordered claude-opus-4.50.
        return ModelCapabilityHeuristics.SupportsExtraHighThinking(modelId);
    }

    /// <summary>
    /// Family heuristic for the extended (1M) context window: the Claude Sonnet 4+ and Opus 4.5+
    /// families advertise it. Config authors targeting an OpenAI-compatible local endpoint (Ollama,
    /// LM Studio) get the standard single-window default unless they declare otherwise.
    /// <para>
    /// #3364: this was a pair of literal id prefixes (<c>claude-sonnet-4</c>, <c>claude-opus-4-5</c>)
    /// which only ever matched the Anthropic-DIRECT id spellings, so a Copilot-served
    /// <c>claude-opus-4.8</c> or <c>claude-opus-5</c> was classified standard-context and the portal
    /// context-window picker had a single tier to choose between. Expressed as a family+version gate
    /// via <see cref="ModelFamilyVersion"/>, both spellings and every future generation resolve
    /// identically without another edit.
    /// </para>
    /// <para>
    /// Deliberately NOT fail-open for an unversioned Claude id, unlike the thinking heuristics in
    /// <see cref="ModelCapabilityHeuristics"/>. The asymmetry is real: guessing thinking forward at
    /// worst produces a loud protocol error, whereas guessing extended context forward OFFERS THE
    /// USER A 1M TIER the model would reject at request time. Widening a picker on a guess is the
    /// failure mode this issue is about, so an unrecognised id keeps its single declared window.
    /// </para>
    /// </summary>
    /// <param name="modelId">The model id.</param>
    /// <returns>True when the family is known to support the extended context window.</returns>
    public static bool InferExtendedContext(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        // Claude Sonnet 4+ and Opus 4.5+ carry the 1M extended window, whether the id arrives in the
        // Anthropic-direct dated spelling (claude-sonnet-4-20250514, claude-opus-4-5-20250929) or the
        // Copilot dotted spelling (claude-sonnet-4.6, claude-opus-4.8, claude-opus-5).
        if (ModelFamilyVersion.IsAtLeast(modelId, "sonnet", SonnetExtendedContextMajor))
            return true;

        if (ModelFamilyVersion.IsAtLeast(modelId, "opus", OpusExtendedContextMajor, OpusExtendedContextMinor))
            return true;

        return false;
    }

    /// <summary>Sonnet gained the 1M extended context window at 4.</summary>
    private const int SonnetExtendedContextMajor = 4;

    /// <summary>Opus gained the 1M extended context window at 4.5.</summary>
    private const int OpusExtendedContextMajor = 4;

    /// <summary>Minor component of the Opus extended-context floor.</summary>
    private const int OpusExtendedContextMinor = 5;
}
