using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using BotNexus.Gateway.Abstractions.Models;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Chat-capability settings for a provider (issue #2854).
///
/// <para><b>Why this exists.</b> Every model-shaped field on <see cref="ProviderConfig"/> means
/// "chat" — <c>defaultModel</c>, <c>models</c>, <c>api</c>, <c>reasoning</c> and the thinking /
/// context flags are all chat semantics sitting on a provider-level object. A provider that serves
/// both chat and embeddings therefore has exactly ONE <c>defaultModel</c> slot for two unrelated
/// model ids, which makes the second capability <em>unrepresentable</em> rather than merely ugly.
/// Moving the chat settings into their own object gives each capability its own namespace.</para>
///
/// <para><b>Additive, not a rename.</b> The flat fields on <see cref="ProviderConfig"/> are retained
/// and remain fully supported; this object <em>overrides</em> them field-by-field when present. A
/// pre-#2854 config document binds and resolves exactly as before — see
/// <see cref="ProviderConfig.EffectiveDefaultModel"/> and its siblings for the precedence rule, and
/// <see cref="PlatformConfigValidator"/> for the deprecation diagnostic naming the replacement path.</para>
/// </summary>
public sealed class ProviderChatConfig
{
    /// <summary>Default chat model for this provider. Overrides the flat <c>providers.*.defaultModel</c>.</summary>
    [Display(
        Name = "Default chat model",
        Description = "Default model identifier used for chat completions when an agent does not specify one. Replaces the provider-level 'defaultModel'.",
        GroupName = "Chat",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "chat", Order = 0, OptionsSource = "models")]
    public string? DefaultModel { get; set; }

    /// <summary>Allowed chat model IDs. Null means all models, empty means none. Overrides the flat <c>providers.*.models</c>.</summary>
    [Display(
        Name = "Chat models",
        Description = "Allowed chat model identifiers for this provider. Null means all models; empty means none. Replaces the provider-level 'models'.",
        GroupName = "Chat",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "chat", Order = 1, OptionsSource = "models")]
    public List<string>? Models { get; set; }

    /// <summary>API identifier used to register this provider's chat models. Overrides the flat <c>providers.*.api</c>.</summary>
    [Display(
        Name = "Chat API",
        Description = "Registered provider API used for chat completions (for example 'openai-completions'). Replaces the provider-level 'api'.",
        GroupName = "Chat",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "chat", Order = 2)]
    public string? Api { get; set; }

    /// <summary>Explicit input modalities for chat models. Overrides the flat <c>providers.*.input</c>.</summary>
    [Display(
        Name = "Input modalities",
        Description = "Explicit input modalities (for example [\"text\",\"image\"]) for this provider's chat models. When unset the modalities are inferred from the model family.",
        GroupName = "Chat",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "chat", Order = 3)]
    public List<string>? Input { get; set; }

    /// <summary>Explicit reasoning capability for chat models. Overrides the flat <c>providers.*.reasoning</c>.</summary>
    [Display(
        Name = "Reasoning",
        Description = "Explicit reasoning/thinking capability for this provider's chat models. When unset it is inferred from the model family.",
        GroupName = "Chat",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "chat", Order = 4)]
    public bool? Reasoning { get; set; }

    /// <summary>Explicit extra-high thinking tier. Overrides the flat <c>providers.*.supportsExtraHighThinking</c>.</summary>
    [Display(
        Name = "Supports extra-high thinking",
        Description = "Explicit extra-high (xhigh/max) thinking-tier capability for this provider's chat models. When unset it is inferred from the model family.",
        GroupName = "Chat",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "chat", Order = 5)]
    public bool? SupportsExtraHighThinking { get; set; }

    /// <summary>Explicit extended (1M) context-window capability. Overrides the flat <c>providers.*.supportsExtendedContextWindow</c>.</summary>
    [Display(
        Name = "Supports extended context window",
        Description = "Explicit extended (1M) context-window capability for this provider's chat models. When unset it is inferred from the model family.",
        GroupName = "Chat",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "chat", Order = 6)]
    public bool? SupportsExtendedContextWindow { get; set; }

    /// <summary>Default context-window size in tokens. Overrides the flat <c>providers.*.contextWindow</c>.</summary>
    [Display(
        Name = "Context window",
        Description = "Default context-window size (in tokens) for this provider's chat models. When unset a conservative 128000-token default is used.",
        GroupName = "Chat",
        Order = 7)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "chat", Order = 7)]
    public int? ContextWindow { get; set; }
}

/// <summary>
/// Embedding-capability settings for a provider (issue #2854).
///
/// <para>Its mere <b>presence</b> is the config-side declaration that this provider does embeddings:
/// per Jon's decision on #2500 the effective capability set is the union of the code-declared set
/// (<see cref="BotNexus.Agent.Providers.Core.Registry.ProviderCapability"/>, #2853) and the
/// config-declared one. There is deliberately no separate <c>capabilities</c> array — a second way to
/// say the same thing is a second way to disagree with it.</para>
///
/// <para><b>Config shape only.</b> Nothing here executes an embedding request; that seam is #2855 and
/// its consumers. This object exists so an embedding model id has somewhere to live that is not the
/// chat provider's single <c>defaultModel</c> slot.</para>
/// </summary>
public sealed class ProviderEmbeddingsConfig
{
    /// <summary>Registered provider API used for embedding requests.</summary>
    [Display(
        Name = "Embeddings API",
        Description = "Registered provider API used for embedding requests (for example 'openai-embeddings').",
        GroupName = "Embeddings",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "embeddings", Order = 0)]
    public string? Api { get; set; }

    /// <summary>Embedding model identifier — separate from the chat default model on purpose.</summary>
    [Display(
        Name = "Embedding model",
        Description = "Model identifier used to generate embeddings (for example 'nomic-embed-text'). Distinct from the chat default model.",
        GroupName = "Embeddings",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "embeddings", Order = 1)]
    public string? Model { get; set; }

    /// <summary>Vector dimensionality produced by <see cref="Model"/>. Null means "use the model's native size".</summary>
    [Display(
        Name = "Dimensions",
        Description = "Vector dimensionality produced by the embedding model. Leave unset to use the model's native size.",
        GroupName = "Embeddings",
        Order = 2)]
    [Range(1, 1_000_000)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "embeddings", Order = 2)]
    public int? Dimensions { get; set; }
}
