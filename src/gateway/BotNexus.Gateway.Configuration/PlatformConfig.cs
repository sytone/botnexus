using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Domain;
using BotNexus.Domain.World;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Platform-wide BotNexus configuration stored at ~/.botnexus/config.json.
/// </summary>
public sealed class PlatformConfig : IValidatableObject
{
    /// <summary>Optional JSON schema reference for editor IntelliSense/validation.</summary>
    [JsonPropertyName("$schema")]
    [Display(
        Name = "Schema",
        Description = "Optional JSON schema reference for editor IntelliSense/validation.",
        GroupName = "General",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "general", Order = 4)]
    public string? Schema { get; set; }

    /// <summary>Configuration schema version for forward compatibility.</summary>
    /// <remarks>
    /// Named "PlatformVersion" (not "Version") to avoid collision with the DOTNET_VERSION
    /// environment variable which the .NET host prefix-strips to just "VERSION" in IConfiguration.
    /// JsonPropertyName keeps config.json reading from the "version" key.
    /// </remarks>
    [JsonPropertyName("version")]
    [Display(
        Name = "Config schema version",
        Description = "Configuration schema version for forward compatibility. Bumped only when the config shape changes incompatibly.",
        GroupName = "General",
        Order = 0)]
    [DefaultValue(1)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "general", Order = 0)]
    public int PlatformVersion { get; set; } = 1;

    /// <summary>
    /// Stable identity of this BotNexus world, generated and persisted on first start (#2834).
    /// </summary>
    /// <remarks>
    /// Present here so the value is a recognised <c>config.json</c> property and appears in the
    /// generated schema and the portal config UI. Runtime consumers must take the injected
    /// <c>WorldIdentity</c> dependency rather than reading this property or the raw configuration key -
    /// the whole point of the token is that it has exactly one derivation.
    /// </remarks>
    [JsonPropertyName("worldId")]
    [Display(
        Name = "World ID",
        Description = "Stable GUID identifying this BotNexus world. Generated automatically on first start; do not copy it between installations.",
        GroupName = "General",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "general", Order = 2)]
    public string? WorldId { get; set; }

    /// <summary>Gateway-specific settings.</summary>
    [Display(
        Name = "Gateway",
        Description = "Gateway-specific settings.",
        GroupName = "General",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "general", Order = 5)]
    public GatewaySettingsConfig? Gateway { get; set; }

    /// <summary>Agent definitions keyed by agent ID.</summary>
    [Display(
        Name = "Agents",
        Description = "Agent definitions keyed by agent ID.",
        GroupName = "General",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "general", Order = 6)]
    public Dictionary<string, AgentDefinitionConfig>? Agents { get; set; }

    /// <summary>Provider configurations keyed by provider name.</summary>
    [Display(
        Name = "Providers",
        Description = "Provider configurations keyed by provider name.",
        GroupName = "General",
        Order = 7)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "general", Order = 7)]
    public Dictionary<string, ProviderConfig>? Providers { get; set; }

    /// <summary>Channel settings keyed by channel name.</summary>
    [Display(
        Name = "Channels",
        Description = "Channel settings keyed by channel name.",
        GroupName = "General",
        Order = 8)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "general", Order = 8)]
    public Dictionary<string, ChannelConfig>? Channels { get; set; }

    /// <summary>API key for Gateway authentication (null = dev mode, no auth).</summary>
    [Display(
        Name = "API key",
        Description = "API key for gateway authentication. Null runs the gateway in dev mode with no auth. Sensitive: stored and shown masked.")]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "general", Order = 1, Secret = true)]
    public string? ApiKey { get; set; }

    /// <summary>Cron scheduler settings and optional seed jobs.</summary>
    [Display(
        Name = "Cron",
        Description = "Cron scheduler settings and optional seed jobs.",
        GroupName = "General",
        Order = 9)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "general", Order = 9)]
    public CronConfig? Cron { get; set; }

    /// <summary>Named prompt templates for CLI rendering and cron template resolution.</summary>
    [Display(
        Name = "Prompt templates",
        Description = "Named prompt templates for CLI rendering and cron template resolution.",
        GroupName = "General",
        Order = 10)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "general", Order = 10)]
    public Dictionary<string, PromptTemplateConfig>? PromptTemplates { get; set; }

    /// <summary>
    /// Workspace and portal display settings (reports, file viewer limits).
    /// </summary>
    [Display(
        Name = "Workspace",
        Description = "Workspace and portal display settings (reports, file viewer limits).",
        GroupName = "General",
        Order = 11)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "general", Order = 11)]
    public WorkspacePortalConfig? Workspace { get; set; }

    /// <summary>
    /// Feature flags, keyed by the names declared in <see cref="FeatureFlags"/> (#2767).
    /// </summary>
    /// <remarks>
    /// <para>Modelled here so <c>botnexus config get/set FeatureManagement.&lt;Flag&gt;</c> can address
    /// flags at all. Previously the section existed only in the raw document, so the CLI rejected
    /// every path under it and the only ways to change a flag were hand-editing config.json or
    /// <c>doctor config</c>'s bespoke raw-JSON write - an unmodelled write path of exactly the kind
    /// that produced the dead <c>compaction</c> block in #2764.</para>
    /// <para><b>The property name is load-bearing and must not be camelCased.</b> Every other
    /// property here is written through <c>JsonNamingPolicy.CamelCase</c>, but
    /// Microsoft.FeatureManagement binds the PascalCase <c>FeatureManagement</c> section, so the
    /// explicit <see cref="JsonPropertyNameAttribute"/> is what stops a write from silently
    /// renaming the section to <c>featureManagement</c> and unbinding every flag while leaving the
    /// file looking correct.</para>
    /// <para>Values are <see cref="JsonElement"/> rather than <see cref="bool"/> because
    /// Microsoft.FeatureManagement accepts either a bool literal or an object carrying an
    /// <c>EnabledFor</c> filter list. A bool-typed dictionary could not represent the filter form,
    /// so a typed round trip would destroy it - the same data-loss shape #2816 fixed for channel
    /// settings.</para>
    /// </remarks>
    [JsonPropertyName(FeatureFlags.SectionName)]
    [Display(
        Name = "Feature flags",
        Description = "Feature flags keyed by name. Each declared flag should carry an explicit true/false; run 'botnexus doctor config' to report any that are absent.",
        GroupName = "General",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "general", Order = 3)]
    public Dictionary<string, JsonElement>? FeatureManagement { get; set; }

    /// <summary>
    /// World-level agent defaults. Populated at load time from the <c>agents.defaults</c> reserved key.
    /// Not directly serialized — extracted separately from the agents dictionary.
    /// </summary>
    [JsonIgnore]
    public AgentDefaultsConfig? AgentDefaults { get; set; }

    /// <summary>
    /// Raw JSON elements for each agent, keyed by agent ID. Used for presence-aware field-level merge.
    /// Populated at load time alongside <see cref="AgentDefaults" />.
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, System.Text.Json.JsonElement>? AgentRawElements { get; set; }

    /// <summary>
    /// Cross-field and graph validation escape hatch for the DataAnnotations pipeline (#1613,
    /// config parity PBI 5/6 of #1579). Per-field scalar rules live as DataAnnotations attributes
    /// (for example <see cref="System.ComponentModel.DataAnnotations.RangeAttribute"/>) directly on
    /// the model and are enforced by <see cref="System.ComponentModel.DataAnnotations.Validator.TryValidateObject"/>.
    /// Rules that span multiple fields, iterate user-keyed dictionaries, or apply conditional
    /// "if X set then Y required" logic cannot be expressed as per-field attributes, so they are
    /// retained imperatively in <see cref="PlatformConfigLoader"/> and surfaced here so a single
    /// <c>TryValidateObject</c> pass enforces both layers identically server-side.
    /// </summary>
    /// <param name="validationContext">The DataAnnotations validation context (unused; the whole
    /// graph is validated from this root instance).</param>
    /// <returns>One <see cref="ValidationResult"/> per cross-field rule violation, with the same
    /// message text the legacy imperative validator produced.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var error in PlatformConfigLoader.CollectCrossFieldErrors(this))
            yield return new ValidationResult(error);
    }

}

/// <summary>Provider-specific configuration.</summary>
public sealed class ProviderConfig
{
    /// <summary>Whether this provider is enabled. Disabled providers are hidden from API.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether this provider is enabled. Disabled providers are hidden from the API.",
        GroupName = "Provider",
        Order = 0)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "provider", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>API key or reference to auth.json entry.</summary>
    [Display(
        Name = "API key",
        Description = "API key for this provider, or a reference to an auth.json entry. Sensitive: stored and shown masked.",
        GroupName = "Provider",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "provider", Order = 1, Secret = true)]
    public string? ApiKey { get; set; }

    /// <summary>Base URL override.</summary>
    [Display(
        Name = "Base URL",
        Description = "Optional base URL override for this provider's API endpoint.",
        GroupName = "Provider",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider", Order = 2)]
    public string? BaseUrl { get; set; }

    /// <summary>Default model for this provider.</summary>
    [Display(
        Name = "Default model",
        Description = "Default model identifier used for this provider when an agent does not specify one.",
        GroupName = "Provider",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "provider", Order = 3, OptionsSource = "models")]
    public string? DefaultModel { get; set; }

    /// <summary>Allowed model IDs for this provider. Null means all models, empty means none.</summary>
    [Display(
        Name = "Models",
        Description = "Allowed model IDs for this provider. Null means all models, empty means none.",
        GroupName = "Provider",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider", Order = 4)]
    public List<string>? Models { get; set; }

    /// <summary>
    /// Explicit input modalities (for example <c>["text","image"]</c>) for models registered from
    /// this provider's <see cref="Models"/> list. When null or empty the modalities are inferred
    /// from each model's family; an explicit declaration always wins. Previously these models were
    /// hardcoded to text-only, so a vision-capable local model silently discarded every image it
    /// was handed (#2485).
    /// </summary>
    [Display(
        Name = "Input",
        Description = "Explicit input modalities (for example [\"text\",\"image\"]) for models registered from this provider's Models list. When null or empty the modalities are inferred from each model's family; an explicit declaration always wins. Previously these models were hardcoded to text-only, so a vision-capable local model silently discarded every image it was handed (#2485).",
        GroupName = "Provider",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider", Order = 5)]
    public List<string>? Input { get; set; }

    /// <summary>
    /// Optional API identifier used when registering models from this provider's <see cref="Models"/>
    /// list. Defaults to <c>"openai-completions"</c> for backward compatibility with config-driven
    /// OpenAI-compatible endpoints (Ollama, LM Studio, etc.). Set to <c>"integration-mock"</c> or
    /// another registered provider's API name to register models against a different
    /// <see cref="BotNexus.Agent.Providers.Core.Registry.IApiProvider"/>.
    /// </summary>
    [Display(
        Name = "API",
        Description = "Optional API identifier used when registering models from this provider's Models list. Defaults to \"openai-completions\" for backward compatibility with config-driven OpenAI-compatible endpoints (Ollama, LM Studio, etc.). Set to \"integration-mock\" or another registered provider's API name to register models against a different IApiProvider.",
        GroupName = "Provider",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider", Order = 6)]
    public string? Api { get; set; }

    /// <summary>
    /// PBI6 (#1707): explicit reasoning/thinking capability for models registered from this
    /// provider's <see cref="Models"/> list. When null (the default) the capability is inferred
    /// from each model's family so a known reasoning family (Claude 4+, GPT-5+, o3/o4, Gemini 3+,
    /// Grok-code) is picked up automatically; set it explicitly for a local model whose id the
    /// family heuristic does not recognise.
    /// </summary>
    [Display(
        Name = "Reasoning",
        Description = "PBI6 (#1707): explicit reasoning/thinking capability for models registered from this provider's Models list. When null (the default) the capability is inferred from each model's family so a known reasoning family (Claude 4+, GPT-5+, o3/o4, Gemini 3+, Grok-code) is picked up automatically; set it explicitly for a local model whose id the family heuristic does not recognise.",
        GroupName = "Provider",
        Order = 7)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "provider", Order = 7)]
    public bool? Reasoning { get; set; }

    /// <summary>
    /// PBI6 (#1707): explicit extra-high (ExtraHigh / Max) thinking-tier capability for this
    /// provider's dynamic models. When null the value is inferred from the model family. Ignored
    /// (clamped off) for a model that does not support reasoning.
    /// </summary>
    [Display(
        Name = "Supports extra high thinking",
        Description = "PBI6 (#1707): explicit extra-high (ExtraHigh / Max) thinking-tier capability for this provider's dynamic models. When null the value is inferred from the model family. Ignored (clamped off) for a model that does not support reasoning.",
        GroupName = "Provider",
        Order = 8)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "provider", Order = 8)]
    public bool? SupportsExtraHighThinking { get; set; }

    /// <summary>
    /// PBI6 (#1707): explicit extended (1M) context-window capability for this provider's dynamic
    /// models. When null the value is inferred from the model family (Anthropic-direct Claude
    /// Sonnet 4/4.5 and Opus 4.5). Drives the context-size picker's second (1M) tier.
    /// </summary>
    [Display(
        Name = "Supports extended context window",
        Description = "PBI6 (#1707): explicit extended (1M) context-window capability for this provider's dynamic models. When null the value is inferred from the model family (Anthropic-direct Claude Sonnet 4/4.5 and Opus 4.5). Drives the context-size picker's second (1M) tier.",
        GroupName = "Provider",
        Order = 9)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "provider", Order = 9)]
    public bool? SupportsExtendedContextWindow { get; set; }

    /// <summary>
    /// PBI6 (#1707): default context-window size (in tokens) for this provider's dynamic models.
    /// When null a conservative 128000-token default is used. Sets the standard tier the
    /// context-size picker offers for a config-declared model.
    /// </summary>
    [Display(
        Name = "Context window",
        Description = "PBI6 (#1707): default context-window size (in tokens) for this provider's dynamic models. When null a conservative 128000-token default is used. Sets the standard tier the context-size picker offers for a config-declared model.",
        GroupName = "Provider",
        Order = 10)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "provider", Order = 10)]
    public int? ContextWindow { get; set; }

    /// <summary>
    /// Chat-capability settings for this provider (#2854). Presence of this object is the
    /// config-side declaration that the provider serves chat.
    /// </summary>
    /// <remarks>
    /// Every field here has a deprecated flat twin above. The flat fields are retained for one
    /// release so an existing <c>config.json</c> keeps working unchanged (AC2); the validator emits
    /// a deprecation warning naming the nested replacement path (AC3). Resolution is per FIELD, not
    /// per object -- see <see cref="ProviderConfigCapabilityExtensions"/> -- so a half-migrated
    /// document that moved <c>defaultModel</c> but not <c>api</c> still resolves both.
    /// </remarks>
    [Display(
        Name = "Chat",
        Description = "Chat-capability settings for this provider (model, allowlist, API and reasoning declarations). Presence of this object declares the provider serves chat.",
        GroupName = "Provider",
        Order = 11)]
    [ConfigField(Group = "provider-chat", Order = 11)]
    public ProviderChatConfig? Chat { get; set; }

    /// <summary>
    /// Embeddings-capability settings for this provider (#2854). Presence of this object is the
    /// config-side declaration that the provider serves embeddings, even when nothing in the code
    /// declares that capability for it.
    /// </summary>
    /// <remarks>
    /// This has no flat twin: before #2854 an embedding model was unrepresentable, because the one
    /// <see cref="DefaultModel"/> slot already meant "chat". That unrepresentability is the reason
    /// the issue exists.
    /// </remarks>
    [Display(
        Name = "Embeddings",
        Description = "Embeddings-capability settings for this provider (embedding model, API and vector dimensions). Presence of this object declares the provider serves embeddings.",
        GroupName = "Provider",
        Order = 12)]
    [ConfigField(Group = "provider-embeddings", Order = 12)]
    public ProviderEmbeddingsConfig? Embeddings { get; set; }
}

/// <summary>
/// Chat-capability settings nested under a provider (#2854).
/// </summary>
/// <remarks>
/// These are the fields that were always chat semantics wearing provider-level clothing. Splitting
/// them out is what makes a second capability representable at all: a provider serving chat AND
/// embeddings previously had exactly one <c>defaultModel</c> slot for two unrelated model ids.
/// </remarks>
public sealed class ProviderChatConfig
{
    /// <summary>API identifier used when registering this provider's chat models.</summary>
    [Display(
        Name = "API",
        Description = "API identifier used when registering this provider's chat models (for example 'openai-completions'). Defaults to 'openai-completions' when omitted.",
        GroupName = "Provider chat",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider-chat", Order = 0)]
    public string? Api { get; set; }

    /// <summary>Default chat model identifier for this provider.</summary>
    [Display(
        Name = "Default model",
        Description = "Default chat model identifier used for this provider when an agent does not specify one.",
        GroupName = "Provider chat",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "provider-chat", Order = 1, OptionsSource = "models")]
    public string? DefaultModel { get; set; }

    /// <summary>Allowed chat model IDs. Null means all models, empty means none.</summary>
    [Display(
        Name = "Models",
        Description = "Allowed chat model IDs for this provider. Null means all models, empty means none.",
        GroupName = "Provider chat",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider-chat", Order = 2)]
    public List<string>? Models { get; set; }

    /// <summary>Explicit input modalities for this provider's chat models.</summary>
    [Display(
        Name = "Input",
        Description = "Explicit input modalities (for example [\"text\",\"image\"]) for this provider's chat models. When null or empty the modalities are inferred from each model's family.",
        GroupName = "Provider chat",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider-chat", Order = 3)]
    public List<string>? Input { get; set; }

    /// <summary>Explicit reasoning/thinking capability for this provider's chat models.</summary>
    [Display(
        Name = "Reasoning",
        Description = "Explicit reasoning/thinking capability for this provider's chat models. When null the capability is inferred from each model's family.",
        GroupName = "Provider chat",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "provider-chat", Order = 4)]
    public bool? Reasoning { get; set; }

    /// <summary>Explicit extra-high (ExtraHigh / Max) thinking-tier capability.</summary>
    [Display(
        Name = "Supports extra high thinking",
        Description = "Explicit extra-high (ExtraHigh / Max) thinking-tier capability for this provider's chat models. When null the value is inferred from the model family.",
        GroupName = "Provider chat",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "provider-chat", Order = 5)]
    public bool? SupportsExtraHighThinking { get; set; }

    /// <summary>Explicit extended (1M) context-window capability.</summary>
    [Display(
        Name = "Supports extended context window",
        Description = "Explicit extended (1M) context-window capability for this provider's chat models. When null the value is inferred from the model family.",
        GroupName = "Provider chat",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "provider-chat", Order = 6)]
    public bool? SupportsExtendedContextWindow { get; set; }

    /// <summary>Default context-window size (in tokens) for this provider's chat models.</summary>
    [Display(
        Name = "Context window",
        Description = "Default context-window size (in tokens) for this provider's chat models. When null a conservative 128000-token default is used.",
        GroupName = "Provider chat",
        Order = 7)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "provider-chat", Order = 7)]
    public int? ContextWindow { get; set; }
}

/// <summary>
/// Embeddings-capability settings nested under a provider (#2854).
/// </summary>
/// <remarks>
/// Config shape and capability declaration only. #2854 is explicitly scoped to make an embeddings
/// endpoint REPRESENTABLE and DISCOVERABLE; executing an embedding request against it is separate
/// work in the #2500 epic, so nothing here is dispatched on yet.
/// </remarks>
public sealed class ProviderEmbeddingsConfig
{
    /// <summary>API identifier serving this provider's embeddings endpoint.</summary>
    [Display(
        Name = "API",
        Description = "API identifier used for this provider's embeddings endpoint (for example 'openai-embeddings').",
        GroupName = "Provider embeddings",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider-embeddings", Order = 0)]
    public string? Api { get; set; }

    /// <summary>The embedding model identifier. Required when an embeddings object is present.</summary>
    [Display(
        Name = "Model",
        Description = "Embedding model identifier served by this provider (for example 'nomic-embed-text'). Required when an embeddings capability is configured.",
        GroupName = "Provider embeddings",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "provider-embeddings", Order = 1)]
    public string? Model { get; set; }

    /// <summary>Vector dimensionality produced by the embedding model.</summary>
    [Display(
        Name = "Dimensions",
        Description = "Vector dimensionality produced by the embedding model (for example 768). Must be greater than zero when specified.",
        GroupName = "Provider embeddings",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "provider-embeddings", Order = 2)]
    public int? Dimensions { get; set; }
}

/// <summary>Gateway runtime configuration.</summary>
public sealed class GatewaySettingsConfig
{
    /// <summary>Gateway HTTP listen URL.</summary>
    [Display(
        Name = "Listen URL",
        Description = "HTTP(S) URL the gateway binds to (for example " + GatewayDefaults.LoopbackListenUrl + "). Supports Kestrel wildcards such as http://+:5000.",
        GroupName = "Gateway",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "gateway", Order = 0)]
    public string? ListenUrl { get; set; }

    /// <summary>
    /// External base URL the portal is reached on, used to build agent-facing deep links such as the
    /// canvas link returned by the <c>canvas</c> tool (#2975).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="ListenUrl"/> on purpose: the gateway commonly binds a wildcard or a
    /// loopback address while users reach it through a tunnel or reverse proxy on a different host.
    /// A link built from the bind address would be undialable, so this is the only value trusted as
    /// an external origin. When it is unset, a CONCRETE listen URL is used and a wildcard bind
    /// yields no link at all.
    /// </remarks>
    [Display(
        Name = "Public base URL",
        Description = "External base URL the portal is reached on (for example https://portal.example.com). Used to build canvas deep links. Leave unset on a purely local install.",
        GroupName = "Gateway",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "gateway", Order = 1)]
    public string? PublicBaseUrl { get; set; }
    /// <summary>Default agent to route to when none specified.</summary>
    [Display(
        Name = "Default agent",
        Description = "Agent ID to route to when an incoming message does not specify one.",
        GroupName = "Gateway",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "gateway", Order = 1)]
    public string? DefaultAgentId { get; set; }
    /// <summary>Path to agents configuration directory.</summary>
    [Display(
        Name = "Agents directory",
        Description = "Directory holding per-agent workspaces and configuration. Relative paths resolve against the BotNexus home directory.",
        GroupName = "Storage",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "storage", Order = 0)]
    public string? AgentsDirectory { get; set; }
    /// <summary>Path to sessions storage directory.</summary>
    [Display(
        Name = "Sessions directory",
        Description = "Directory holding session transcripts and the session store database.",
        GroupName = "Storage",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "storage", Order = 1)]
    public string? SessionsDirectory { get; set; }
    /// <summary>Session store selection and configuration.</summary>
    [Display(
        Name = "Session store",
        Description = "Backend used to persist sessions and conversation history.",
        GroupName = "Storage",
        Order = 2)]
    [ConfigField(Group = "storage", Order = 2)]
    public SessionStoreConfig? SessionStore { get; set; }

    /// <summary>Interval in minutes between periodic PASSIVE SQLite WAL checkpoints (#1438). Default 30.</summary>
    [Display(
        Name = "WAL checkpoint interval (min)",
        Description = "Minutes between periodic PASSIVE SQLite WAL checkpoints. A TRUNCATE checkpoint also runs on graceful shutdown. Default 30.",
        GroupName = "Gateway",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "gateway", Order = 3)]
    public int? WalCheckpointIntervalMinutes { get; set; }
    /// <summary>Trusted sub-agent spawning limits and per-parent budget overrides.</summary>
    [Display(
        Name = "Sub-agents",
        Description = "Limits on sub-agent spawning, including per-parent turn and timeout budgets.",
        GroupName = "Agents",
        Order = 0)]
    [ConfigField(Group = "sub-agents", Order = 0)]
    public SubAgentOptions? SubAgents { get; set; }
    /// <summary>Session compaction settings.</summary>
    [Display(
        Name = "Compaction",
        Description = "Controls when a long session is summarised to stay within the model context window.",
        GroupName = "Sessions",
        Order = 0)]
    [ConfigField(Group = "compaction", Order = 0)]
    public CompactionOptions? Compaction { get; set; }
    /// <summary>Write-time cap on the size of individual tool results persisted to session history (#1598).</summary>
    [Display(
        Name = "Tool result persistence",
        Description = "Write-time cap on the size of individual tool results persisted to session history.",
        GroupName = "Sessions",
        Order = 1)]
    [ConfigField(Group = "tool-result-persistence", Order = 0)]
    public ToolResultPersistenceConfig? ToolResultPersistence { get; set; }
    /// <summary>Central backstop budget on tool-result size returned to the model (#3162).</summary>
    [Display(
        Name = "Tool output budget",
        Description = "Limits on how much output a single tool result may contribute before it is truncated.")]
    [ConfigField(Group = "tool-output-budget", Order = 0)]
    public ToolOutputBudgetConfig? ToolOutputBudget { get; set; }
    /// <summary>Size guardrails for the <c>read</c> tool (#2689).</summary>
    [Display(
        Name = "Read tool",
        Description = "Settings governing the built-in file read tool, including paging limits.")]
    [ConfigField(Group = "read-tool", Order = 0)]
    public ReadToolConfig? ReadTool { get; set; }
    /// <summary>Post-turn claim auditor (anti-fabrication) settings (#1600).</summary>
    [Display(
        Name = "Claim audit",
        Description = "Post-turn auditor that checks an agent's stated outcomes against the tool calls it actually made.",
        GroupName = "Agents",
        Order = 1)]
    [ConfigField(Group = "claim-audit", Order = 0)]
    public ClaimAuditConfig? ClaimAudit { get; set; }
    /// <summary>
    /// Memory embedding backend selection (#2855). Absent or disabled leaves memory retrieval
    /// lexical-only, which is the default the platform has shipped since #2356.
    /// </summary>
    [Display(
        Name = "Memory embeddings",
        Description = "Embedding backend supplying vectors for hybrid memory retrieval. Absent or disabled keeps retrieval lexical-only.",
        GroupName = "Memory embeddings",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "memory-embeddings", Order = 0)]
    public MemoryEmbeddingsConfig? MemoryEmbeddings { get; set; }
    /// <summary>CORS settings for browser-based clients.</summary>
    [Display(
        Name = "CORS",
        Description = "Cross-origin request rules for browser-based clients reaching the gateway API.",
        GroupName = "Network",
        Order = 0)]
    [ConfigField(Group = "network", Order = 0)]
    public CorsConfig? Cors { get; set; }
    /// <summary>Per-client request rate limiting settings.</summary>
    [Display(
        Name = "Rate limiting",
        Description = "Per-client request rate limits applied to gateway API calls.",
        GroupName = "Network",
        Order = 1)]
    [ConfigField(Group = "network", Order = 1)]
    public RateLimitConfig? RateLimit { get; set; }
    /// <summary>Explicit SignalR hub transport limits (frame size, parallel invocations, stream buffer).</summary>
    [Display(
        Name = "SignalR transport",
        Description = "Hub transport limits: maximum frame size, parallel invocations, and stream buffer capacity.",
        GroupName = "Network",
        Order = 2)]
    [ConfigField(Group = "network", Order = 2)]
    public SignalRConfig? SignalR { get; set; }
    /// <summary>Operator-supplied additional secret redaction patterns (#2727).</summary>
    [Display(
        Name = "Secret redaction",
        Description = "Settings controlling how secrets are detected and masked in logs and tool output.")]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "gateway", Order = 10)]
    public SecretRedactionConfig? SecretRedaction { get; set; }

    /// <summary>Logging level override.</summary>
    [Display(
        Name = "Log level",
        Description = "Minimum log level override. One of Trace, Debug, Information, Warning, Error, Critical.",
        GroupName = "Gateway",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "gateway", Order = 2)]
    public string? LogLevel { get; set; }
    /// <summary>Multi-tenant API keys keyed by key ID.</summary>
    [Display(
        Name = "API keys",
        Description = "Multi-tenant API keys keyed by key ID. Each entry authorises a caller and scopes what it may reach.",
        GroupName = "Security",
        Order = 0)]
    // #3654: the CONTAINER is not itself a secret -- the secret is ApiKeyConfig.ApiKey, which
    // carries its own [ConfigField(Secret = true)] and is reached through the "*" wildcard
    // recursion. Marking the dictionary secret made discovery emit a SecretTerminal.Scalar path
    // (it is Dictionary<string, ApiKeyConfig>, not Dictionary<string, string>), and ApplyRedact
    // only overwrites a JsonValue -- so the path was a silent no-op that redacted nothing while
    // still stamping x-ui-secret on an object-valued node in the generated schema.
    // Redaction parity is covered by ConfigControllerTests
    // .GetSection_GatewaySection_RedactsApiKeysConnectionStringsAndCrossWorldSecrets.
    [ConfigField(Group = "security", Order = 0)]
    public Dictionary<string, ApiKeyConfig>? ApiKeys { get; set; }
    /// <summary>Extensions loading settings.</summary>
    [Display(
        Name = "Extensions",
        Description = "Dynamic extension loading: whether extensions load, from where, and their world-level defaults.",
        GroupName = "Extensions",
        Order = 0)]
    [ConfigField(Group = "extensions", Order = 0)]
    public ExtensionsConfig? Extensions { get; set; }
    /// <summary>World identity shown by gateway clients.</summary>
    [Display(
        Name = "World identity",
        Description = "Name and identity of this world as shown by gateway clients and used in cross-world federation.",
        GroupName = "World",
        Order = 0)]
    [ConfigField(Group = "world", Order = 0)]
    public WorldIdentity? World { get; set; }
    /// <summary>Named locations registry for resource management.</summary>
    [Display(
        Name = "Locations",
        Description = "Named locations registry used for resource management and path resolution.",
        GroupName = "Storage",
        Order = 3)]
    [ConfigField(Group = "storage", Order = 3)]
    public Dictionary<string, LocationConfig>? Locations { get; set; }
    /// <summary>Optional explicit cross-world communication permissions.</summary>
    [Display(
        Name = "Cross-world permissions",
        Description = "Explicit grants controlling which remote worlds may communicate with this one.",
        GroupName = "World",
        Order = 2)]
    [ConfigField(Group = "world", Order = 2)]
    public List<CrossWorldPermissionConfig>? CrossWorldPermissions { get; set; }
    /// <summary>Cross-world federation settings for gateway-to-gateway communication.</summary>
    [Display(
        Name = "Cross-world federation",
        Description = "Gateway-to-gateway federation settings enabling communication between worlds.",
        GroupName = "World",
        Order = 1)]
    [ConfigField(Group = "world", Order = 1)]
    public CrossWorldFederationConfig? CrossWorld { get; set; }
    /// <summary>Default file access policy applied to all agents unless overridden per-agent.</summary>
    [Display(
        Name = "File access policy",
        Description = "Default read, write and deny path rules applied to every agent unless the agent overrides them.",
        GroupName = "Security",
        Order = 1)]
    [ConfigField(Group = "security", Order = 1)]
    public FileAccessPolicyConfig? FileAccess { get; set; }
    /// <summary>
    /// Preferred shell for command execution on Windows.
    /// Values: <c>"auto"</c> (default — bash when available, PowerShell fallback),
    /// <c>"pwsh"</c> (always PowerShell), <c>"bash"</c> (always bash).
    /// Ignored on non-Windows platforms where bash is always used.
    /// </summary>
    [Display(
        Name = "Shell preference",
        Description = "Preferred shell on Windows: auto (bash when available, PowerShell otherwise), pwsh, or bash. Ignored on non-Windows platforms.",
        GroupName = "Execution",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "execution", Order = 0)]
    public string? ShellPreference { get; set; }

    /// <summary>
    /// Custom shell command array for command execution.
    /// Element [0] is the executable, remaining elements are base arguments.
    /// The agent's command string is appended as the final argument.
    /// Example: <c>["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]</c>.
    /// When set, overrides <see cref="ShellPreference"/> entirely.
    /// </summary>
    [Display(
        Name = "Shell command",
        Description = "Explicit shell argv array. Element 0 is the executable; the command string is appended last. Overrides Shell preference entirely when set.",
        GroupName = "Execution",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "execution", Order = 1)]
    public string[]? ShellCommand { get; set; }

    /// <summary>Auto-update settings for self-updating the gateway via the BotNexus CLI.</summary>
    [Display(
        Name = "Auto-update",
        Description = "Settings for the gateway updating itself via the BotNexus CLI.",
        GroupName = "Maintenance",
        Order = 0)]
    [ConfigField(Group = "auto-update", Order = 0)]
    public AutoUpdateConfig? AutoUpdate { get; set; }

    /// <summary>
    /// Auxiliary (cheap/fast) model configuration for background gateway tasks.
    /// Currently used for: conversation title generation.
    /// </summary>
    [Display(
        Name = "Auxiliary model",
        Description = "Cheap, fast model used for background gateway tasks such as conversation title generation.",
        GroupName = "Gateway",
        Order = 11)]
    [ConfigField(Group = "auxiliary", Order = 0)]
    public AuxiliaryConfig? Auxiliary { get; set; }

    /// <summary>
    /// Server-wide default IANA timezone ID used when an agent has no Soul timezone configured.
    /// Falls back to UTC when null or invalid.
    /// Example: <c>"America/Los_Angeles"</c>.
    /// </summary>
    [Display(
        Name = "Default timezone",
        Description = "Server-wide default IANA timezone ID used when an agent has no timezone configured. Falls back to UTC when blank or invalid.",
        GroupName = "Gateway",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "gateway", Order = 3)]
    public string? DefaultTimezone { get; set; }

    /// <summary>
    /// When true, all provider HTTP requests and responses are logged at Debug level.
    /// Auth headers are always redacted. Response bodies are not buffered for streaming calls.
    /// Off by default; enable only for debugging unexpected provider responses.
    /// </summary>
    [Display(
        Name = "Log provider requests",
        Description = "When on, all provider HTTP requests and responses are logged at Debug level. Auth headers are always redacted. For debugging only.",
        GroupName = "Gateway",
        Order = 4)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "gateway", Order = 4)]
    public bool EnableProviderRequestLogging { get; set; } = false;

    /// <summary>
    /// Server-wide datetime injection settings. When enabled, the current datetime is prepended
    /// to every user message sent to the LLM so agents always know the current time.
    /// Per-agent overrides take precedence over this world default.
    /// </summary>
    [Display(
        Name = "Datetime injection",
        Description = "World default for prepending the current datetime to user messages. Per-agent settings take precedence.",
        GroupName = "Agents",
        Order = 2)]
    [ConfigField(Group = "datetime-injection", Order = 0)]
    public DateTimeInjectionConfig? DateTimeInjection { get; set; }

    /// <summary>
    /// Registered satellite nodes keyed by satellite ID.
    /// Satellites are remote persistent processes that connect to the gateway for
    /// notifications, canvas rendering, and optionally remote command execution.
    /// </summary>
    [Display(
        Name = "Satellites",
        Description = "Registered satellite nodes keyed by satellite ID. Satellites connect for notifications, canvas rendering and optional remote execution.",
        GroupName = "Network",
        Order = 3)]
    [ConfigField(Group = "satellites", Order = 0)]
    public Dictionary<string, SatelliteConfig>? Satellites { get; set; }
}

/// <summary>
/// Write-time tool-result size cap (#1598). Large tool results (e.g. a recursive directory
/// listing or a session-history dump) are otherwise persisted into <c>session_history</c> at
/// full size and re-sent to the model on every subsequent turn, consuming context budget with
/// zero ongoing value. When enabled, a result exceeding <see cref="MaxBytes"/> UTF-8 bytes is
/// truncated at write time (on a rune boundary) with an explicit <c>[truncated N bytes]</c> marker.
/// </summary>
public sealed class ToolResultPersistenceConfig
{
    /// <summary>
    /// Whether the write-time tool-result cap is enabled. Defaults to <see langword="true"/>.
    /// </summary>
    [Display(
        Name = "Enabled",
        Description = "Whether the write-time tool-result cap is enabled. Defaults to .",
        GroupName = "Tool result persistence",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "tool-result-persistence", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum UTF-8 byte size of a single persisted tool result. Results larger than this are
    /// truncated at write time with a <c>[truncated N bytes]</c> marker. Defaults to 16384 (16 KiB).
    /// A value of 0 or less disables truncation even when <see cref="Enabled"/> is true.
    /// </summary>
    [Display(
        Name = "Max bytes",
        Description = "Maximum UTF-8 byte size of a single persisted tool result. Results larger than this are truncated at write time with a [truncated N bytes] marker. Defaults to 16384 (16 KiB). A value of 0 or less disables truncation even when Enabled is true.",
        GroupName = "Tool result persistence",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "tool-result-persistence", Order = 1)]
    public int MaxBytes { get; set; } = 16_384;
}

/// <summary>
/// Central backstop budget on the size of a tool result returned to the model (#3162).
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="ToolResultPersistenceConfig"/>, which bounds what is WRITTEN to session
/// history after the fact. By then the model has already received the full payload. This budget is
/// applied in the agent loop's tool executor, so it bounds what reaches the context window in the
/// first place, for every tool regardless of origin -- including MCP-bridged tools, which carry no
/// size limit of their own.
/// </para>
/// <para>
/// It is a backstop beneath the existing per-tool caps, not a replacement: the default is larger
/// than every first-party per-tool cap, so a tool that already bounds its own output never trips it.
/// An oversize result is returned as a bounded SUCCESS with a truncation marker, the omitted byte
/// count and one consistent line of narrowing guidance -- never as an error and never silently
/// dropped.
/// </para>
/// </remarks>
public sealed class ToolOutputBudgetConfig
{
    /// <summary>
    /// Default UTF-8 byte budget (256 KiB). Deliberately larger than every first-party per-tool
    /// cap so this backstop never retunes one of them.
    /// </summary>
    public const int DefaultMaxBytes = 256 * 1024;

    /// <summary>
    /// Whether the central tool-output backstop is enabled. Defaults to <see langword="true"/>.
    /// </summary>
    [Display(
        Name = "Enable tool output budget",
        Description = "Bounds every tool result returned to the model, regardless of which tool produced it. Defaults to enabled.",
        GroupName = "Tool output budget",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "tool-output-budget", Order = 0)]
    [DefaultValue(true)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Maximum UTF-8 byte size of a single tool result returned to the model. Defaults to 262144
    /// (256 KiB). A value of 0 or less disables the backstop even when <see cref="Enabled"/> is
    /// true, matching the <see cref="ToolResultPersistenceConfig.MaxBytes"/> convention.
    /// </summary>
    [Display(
        Name = "Max tool output bytes",
        Description = "Maximum UTF-8 byte size of a single tool result returned to the model. Defaults to 262144 (256 KiB). Zero or less disables the backstop.",
        GroupName = "Tool output budget",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "tool-output-budget", Order = 1)]
    [DefaultValue(DefaultMaxBytes)]
    public int MaxBytes { get; set; } = DefaultMaxBytes;
}

/// <summary>
/// Size guardrails for the <c>read</c> tool (#2689).
/// </summary>
/// <remarks>
/// Distinct from <see cref="ToolOutputBudgetConfig"/>, which is a hard 256 KiB backstop applied to
/// every tool after the fact. These settings are advisory and read-path-only: they attach guidance
/// to a large result and elide an identical unchanged re-read, so an agent pays the whole-file cost
/// once rather than repeatedly. They never truncate content and never change what <c>read</c> can
/// express.
/// </remarks>
public sealed class ReadToolConfig
{
    /// <summary>Default size-notice threshold in UTF-8 bytes (20 KiB).</summary>
    public const int DefaultLargeReadThresholdBytes = 20 * 1024;

    /// <summary>
    /// UTF-8 byte size above which a <c>read</c> result carries an explicit size indicator naming
    /// <c>offset</c> and <c>limit</c> as the narrowing controls. Defaults to 20480 (20 KiB). Zero or
    /// less disables the indicator, matching the <see cref="ToolOutputBudgetConfig.MaxBytes"/>
    /// convention.
    /// </summary>
    [Display(
        Name = "Large read threshold bytes",
        Description = "UTF-8 byte size above which a read result carries a size indicator naming offset and limit. Defaults to 20480 (20 KiB). Zero or less disables it.",
        GroupName = "Read tool",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "read-tool", Order = 0)]
    [DefaultValue(DefaultLargeReadThresholdBytes)]
    public int LargeReadThresholdBytes { get; set; } = DefaultLargeReadThresholdBytes;

    /// <summary>
    /// Whether an identical re-read of an UNCHANGED file slice in the same session returns a short
    /// marker instead of the full body. Defaults to <see langword="true"/>. A changed file always
    /// returns fresh content: the file is re-read from disk on every call and elision only applies
    /// when the fresh content hashes identically to what was already shown.
    /// </summary>
    [Display(
        Name = "Elide unchanged re-reads",
        Description = "Return a short marker instead of the full body when the same slice of an unchanged file is re-read in one session. A changed file always returns fresh content.",
        GroupName = "Read tool",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "read-tool", Order = 1)]
    [DefaultValue(true)]
    public bool ElideUnchangedRereads { get; set; } = true;
}

/// <summary>
/// Configuration for the post-turn claim auditor (#1600, control #1 of #1551). The auditor scans
/// the agent's final user-facing message for artifact-shaped claims (a GitHub issue was filed, a PR
/// opened, a file written, something sent/deployed, an audit "verified") and flags any claim that
/// has no backing tool call among the tools actually invoked during the run. This inverts the
/// trust model that failed when an agent narrated "filed issue #N" with no tool call that turn:
/// it verifies rather than trusting narration.
/// </summary>
public sealed class ClaimAuditConfig
{
    /// <summary>
    /// Whether the post-turn claim auditor runs. Defaults to <see langword="true"/>.
    /// </summary>
    [Display(
        Name = "Enabled",
        Description = "Whether the post-turn claim auditor runs. Defaults to .",
        GroupName = "Claim audit",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "claim-audit", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Reaction on detecting an unbacked claim: <c>"warn"</c> (emit an observable signal only,
    /// the safe default) or <c>"block"</c> (also mark the turn as one that should be blocked).
    /// Unrecognised values fall back to <c>"warn"</c>.
    /// </summary>
    [Display(
        Name = "Mode",
        Description = "Reaction on detecting an unbacked claim: \"warn\" (emit an observable signal only, the safe default) or \"block\" (also mark the turn as one that should be blocked). Unrecognised values fall back to \"warn\".",
        GroupName = "Claim audit",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "claim-audit", Order = 1)]
    public string Mode { get; set; } = "warn";
}

/// <summary>
/// Configuration for the auto-update feature that polls GitHub and spawns the CLI updater.
/// When <see cref="Enabled"/> is true, both <see cref="CliPath"/> and <see cref="SourcePath"/>
/// must be provided or <c>POST /api/gateway/update/start</c> will return 412.
/// </summary>
public sealed class AutoUpdateConfig
{
    /// <summary>Enables background GitHub polling and the update endpoint. Defaults to false.</summary>
    [Display(
        Name = "Enable auto-update",
        Description = "Enables background GitHub polling and the self-update endpoint.",
        GroupName = "Auto-update",
        Order = 0)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "auto-update", Order = 0)]
    public bool Enabled { get; set; } = false;

    /// <summary>How often to poll GitHub for a new commit. Minimum 5. Defaults to 60.</summary>
    [Display(
        Name = "Check interval (minutes)",
        Description = "How often to poll GitHub for a new commit, in minutes. Minimum 5.",
        GroupName = "Auto-update",
        Order = 1)]
    [DefaultValue(60)]
    [Range(5, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "auto-update", Order = 1)]
    public int CheckIntervalMinutes { get; set; } = 60;

    /// <summary>GitHub repository owner. Defaults to <c>sytone</c>.</summary>
    [Display(
        Name = "Repository owner",
        Description = "GitHub repository owner. Defaults to sytone.",
        GroupName = "Auto-update",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "auto-update", Order = 2)]
    public string RepositoryOwner { get; set; } = "sytone";

    /// <summary>GitHub repository name. Defaults to <c>botnexus</c>.</summary>
    [Display(
        Name = "Repository name",
        Description = "GitHub repository name. Defaults to botnexus.",
        GroupName = "Auto-update",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "auto-update", Order = 3)]
    public string RepositoryName { get; set; } = "botnexus";

    /// <summary>Branch to track. Defaults to <c>main</c>.</summary>
    [Display(
        Name = "Branch",
        Description = "Branch to track. Defaults to main.",
        GroupName = "Auto-update",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "auto-update", Order = 4)]
    public string Branch { get; set; } = "main";

    /// <summary>
    /// Absolute path to the BotNexus CLI entry point used to run the update.
    /// Required when <see cref="Enabled"/> is true.
    /// If the path ends with <c>.dll</c> it is launched via <c>dotnet</c>; otherwise it is run directly.
    /// </summary>
    [Display(
        Name = "Cli path",
        Description = "Absolute path to the BotNexus CLI entry point used to run the update. Required when Enabled is true. If the path ends with .dll it is launched via dotnet; otherwise it is run directly.",
        GroupName = "Auto-update",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "auto-update", Order = 5)]
    public string? CliPath { get; set; }

    /// <summary>
    /// Absolute path to the BotNexus source tree. Passed to the CLI update command as <c>--source</c>.
    /// Required when <see cref="Enabled"/> is true.
    /// </summary>
    [Display(
        Name = "Source path",
        Description = "Absolute path to the BotNexus source tree. Passed to the CLI update command as --source. Required when Enabled is true.",
        GroupName = "Auto-update",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "auto-update", Order = 6)]
    public string? SourcePath { get; set; }

    /// <summary>
    /// Update channel to forward to the CLI update command. Typical values: <c>stable</c>, <c>beta</c>, <c>dev</c>.
    /// When null or empty the CLI default channel is used.
    /// </summary>
    [Display(
        Name = "Channel",
        Description = "Update channel to forward to the CLI update command. Typical values: stable, beta, dev. When null or empty the CLI default channel is used.",
        GroupName = "Auto-update",
        Order = 7)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "auto-update", Order = 7)]
    public string? Channel { get; set; }

    /// <summary>Seconds to wait after returning 202 before calling StopApplication(). Minimum 1. Defaults to 2.</summary>
    [Display(
        Name = "Shutdown delay seconds",
        Description = "Seconds to wait after returning 202 before calling StopApplication(). Minimum 1. Defaults to 2.",
        GroupName = "Auto-update",
        Order = 8)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "auto-update", Order = 8)]
    public int ShutdownDelaySeconds { get; set; } = 2;
}

/// <summary>Named location configuration for resource access.</summary>
public sealed class LocationConfig
{
    /// <summary>Location type: filesystem, api, mcp-server, database, remote-node.</summary>
    [Display(
        Name = "Type",
        Description = "What kind of resource this is: filesystem, api, mcp-server, database or remote-node. Determines which of the fields below are required.",
        GroupName = "Location",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "location", Order = 0)]
    public string Type { get; set; } = "filesystem";

    /// <summary>Path for filesystem locations.</summary>
    [Display(
        Name = "Path",
        Description = "Filesystem path. Required for filesystem locations. A leading ~ expands to the user's home directory.",
        GroupName = "Location",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "location", Order = 1)]
    public string? Path { get; set; }

    /// <summary>Endpoint URL for api/mcp-server/remote-node locations.</summary>
    [Display(
        Name = "Endpoint",
        Description = "Absolute http or https URL. Required for api, mcp-server and remote-node locations.",
        GroupName = "Location",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "location", Order = 2)]
    public string? Endpoint { get; set; }

    /// <summary>Connection string for database locations.</summary>
    [Display(
        Name = "Connection string",
        Description = "Database connection string. Required for database locations. Held in full in config.json, so prefer credentialRef where the target supports it.",
        GroupName = "Location",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "location", Order = 3, Secret = true)]
    public string? ConnectionString { get; set; }

    /// <summary>Account name to authenticate as. Not a secret.</summary>
    [Display(
        Name = "Username",
        Description = "Account to authenticate as, for example automation@pve. Stored in the clear - it is an identity, not a credential.",
        GroupName = "Location",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "location", Order = 4)]
    public string? Username { get; set; }

    /// <summary>
    /// Reference to the credential for this location, as <c>scheme:identifier</c> — for example
    /// <c>env:PROXMOX_TOKEN</c> or <c>file:~/.botnexus/secrets/proxmox</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <b>not</b> marked <c>Secret</c>, and rendered as ordinary text. It holds a
    /// pointer, never a credential: a value with no scheme fails validation precisely so a pasted
    /// password cannot end up here. Masking it would hide the one part an operator needs to read
    /// to fix a mis-typed reference, while protecting nothing.
    /// </para>
    /// <para>
    /// The credential itself is resolved at call time by <c>ISecretResolver</c> and is never held
    /// in configuration, never echoed by the API, and never placed in an agent's context.
    /// </para>
    /// </remarks>
    [Display(
        Name = "Credential reference",
        Description = "Where to find this location's credential, as scheme:identifier - for example env:PROXMOX_TOKEN or file:~/.botnexus/secrets/proxmox. This field holds a reference, never the credential itself.",
        GroupName = "Location",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "location", Order = 5)]
    public string? CredentialRef { get; set; }

    /// <summary>Whether to verify the endpoint's TLS certificate. Defaults to true.</summary>
    /// <remarks>
    /// Defaults to verifying. A homelab target with a self-signed certificate is the usual reason
    /// to turn this off, and making that an explicit, visible setting is better than the
    /// alternative everyone reaches for otherwise, which is disabling verification globally.
    /// </remarks>
    [Display(
        Name = "Verify TLS",
        Description = "Verify the endpoint's TLS certificate. Turn off only for a target using a self-signed certificate, and only for that target.",
        GroupName = "Location",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "location", Order = 6)]
    public bool VerifyTls { get; set; } = true;

    /// <summary>Human-readable description.</summary>
    [Display(
        Name = "Description",
        Description = "What this location is, in a few words. Shown wherever the location is listed.",
        GroupName = "Location",
        Order = 7)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "location", Order = 7)]
    public string? Description { get; set; }

    /// <summary>Free-form labels for grouping and filtering locations.</summary>
    [Display(
        Name = "Tags",
        Description = "Free-form labels for grouping and filtering, for example homelab or hypervisor.",
        GroupName = "Location",
        Order = 8)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "location", Order = 8)]
    public List<string>? Tags { get; set; }

    /// <summary>Extensible properties.</summary>
    [Display(
        Name = "Properties",
        Description = "Extra key/value settings a consumer of this location understands, for example a Proxmox node name.",
        GroupName = "Location",
        Order = 9)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "location", Order = 9)]
    public Dictionary<string, string>? Properties { get; set; }
}

/// <summary>Configuration for granting communication with another world.</summary>
public sealed class CrossWorldPermissionConfig
{
    /// <summary>Identifier of the target world this permission applies to.</summary>
    [Display(
        Name = "Target world ID",
        Description = "Identifier of the target world this permission applies to.",
        GroupName = "Cross world permission",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world-permission", Order = 0)]
    public string? TargetWorldId { get; set; }

    /// <summary>Specific agents allowed to communicate. Null means all hosted agents.</summary>
    [Display(
        Name = "Allowed agents",
        Description = "Specific agents allowed to communicate. Null means all hosted agents.",
        GroupName = "Cross world permission",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world-permission", Order = 1)]
    public List<string>? AllowedAgents { get; set; }

    /// <summary>Whether inbound communication from the target world is allowed.</summary>
    [Display(
        Name = "Allow inbound",
        Description = "Whether inbound communication from the target world is allowed.",
        GroupName = "Cross world permission",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "cross-world-permission", Order = 2)]
    public bool AllowInbound { get; set; } = true;

    /// <summary>Whether outbound communication to the target world is allowed.</summary>
    [Display(
        Name = "Allow outbound",
        Description = "Whether outbound communication to the target world is allowed.",
        GroupName = "Cross world permission",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "cross-world-permission", Order = 3)]
    public bool AllowOutbound { get; set; } = true;
}

/// <summary>Cross-world federation runtime configuration.</summary>
public sealed class CrossWorldFederationConfig
{
    /// <summary>Known peer gateways keyed by world ID or alias.</summary>
    [Display(
        Name = "Peers",
        Description = "Known peer gateways keyed by world ID or alias.",
        GroupName = "Cross world federation",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world-federation", Order = 0)]
    public Dictionary<string, CrossWorldPeerConfig>? Peers { get; set; }

    /// <summary>Inbound cross-world relay policy.</summary>
    [Display(
        Name = "Inbound",
        Description = "Inbound cross-world relay policy.",
        GroupName = "Cross world federation",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "cross-world-federation", Order = 1)]
    public CrossWorldInboundConfig? Inbound { get; set; }

    /// <summary>Optional explicit cross-world agent discovery map.</summary>
    [Display(
        Name = "Agents",
        Description = "Optional explicit cross-world agent discovery map.",
        GroupName = "Cross world federation",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world-federation", Order = 2)]
    public Dictionary<string, CrossWorldAgentConfig>? Agents { get; set; }
}

/// <summary>Outbound peer gateway settings.</summary>
public sealed class CrossWorldPeerConfig
{
    /// <summary>Canonical world ID for this peer (defaults to dictionary key).</summary>
    [Display(
        Name = "World ID",
        Description = "Canonical world ID for this peer (defaults to dictionary key).",
        GroupName = "Cross world peer",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world-peer", Order = 3)]
    public string? WorldId { get; set; }

    /// <summary>Peer gateway endpoint URL.</summary>
    [Display(
        Name = "Endpoint",
        Description = "Peer gateway endpoint URL.",
        GroupName = "Cross world peer",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world-peer", Order = 4)]
    public string? Endpoint { get; set; }

    /// <summary>Shared API key used for gateway-to-gateway relay authentication.</summary>
    [Display(
        Name = "API key",
        Description = "API key used to authenticate to this peer world. Sensitive: stored and shown masked.")]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "cross-world-peer", Order = 2, Secret = true)]
    public string? ApiKey { get; set; }

    /// <summary>Whether this peer is enabled for outbound calls.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether this peer is enabled for outbound calls.",
        GroupName = "Cross world peer",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "cross-world-peer", Order = 5)]
    public bool Enabled { get; set; } = true;
}

/// <summary>Inbound cross-world relay authentication and allow-list policy.</summary>
public sealed class CrossWorldInboundConfig
{
    /// <summary>Whether inbound relay endpoint is enabled.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether inbound relay endpoint is enabled.",
        GroupName = "Cross world inbound",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "cross-world-inbound", Order = 3)]
    public bool Enabled { get; set; } = true;

    /// <summary>Allowed source world IDs. Empty means no source worlds are allowed.</summary>
    [Display(
        Name = "Allowed worlds",
        Description = "Allowed source world IDs. Empty means no source worlds are allowed.",
        GroupName = "Cross world inbound",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world-inbound", Order = 4)]
    public List<string>? AllowedWorlds { get; set; }

    /// <summary>Shared API keys keyed by source world ID.</summary>
    [Display(
        Name = "Inbound API keys",
        Description = "API keys accepted from peer worlds, keyed by peer name. Sensitive: stored and shown masked.")]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "cross-world-inbound", Order = 2, Secret = true)]
    public Dictionary<string, string>? ApiKeys { get; set; }
}

/// <summary>Explicit cross-world agent discovery record.</summary>
public sealed class CrossWorldAgentConfig
{
    /// <summary>Target world hosting the agent.</summary>
    [Display(
        Name = "World ID",
        Description = "Target world hosting the agent.",
        GroupName = "Cross world",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world", Order = 0)]
    public string? WorldId { get; set; }

    /// <summary>Remote agent ID within the target world.</summary>
    [Display(
        Name = "Agent ID",
        Description = "Remote agent ID within the target world.",
        GroupName = "Cross world",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world", Order = 1)]
    public string? AgentId { get; set; }

    /// <summary>Optional operator-facing description.</summary>
    [Display(
        Name = "Description",
        Description = "Optional operator-facing description.",
        GroupName = "Cross world",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cross-world", Order = 2)]
    public string? Description { get; set; }
}

/// <summary>CORS settings for gateway HTTP endpoints.</summary>
public sealed class CorsConfig
{
    /// <summary>Explicit origins allowed to access the gateway from browsers.</summary>
    [Display(
        Name = "Allowed origins",
        Description = "Explicit origins allowed to access the gateway from browsers.",
        GroupName = "CORS",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cors", Order = 0)]
    public List<string>? AllowedOrigins { get; set; }
}

/// <summary>Rate limiting settings for gateway HTTP endpoints.</summary>
public sealed class RateLimitConfig
{
    /// <summary>Whether rate limiting is active. Defaults to false (disabled).</summary>
    [Display(
        Name = "Enable rate limiting",
        Description = "Whether per-client request rate limiting is active.",
        GroupName = "Rate limit",
        Order = 0)]
    [DefaultValue(false)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "rate-limit", Order = 0)]
    public bool Enabled { get; set; }

    /// <summary>Maximum requests allowed in a window for a single client.</summary>
    [Display(
        Name = "Requests per minute",
        Description = "Maximum requests allowed within a window for a single client.",
        GroupName = "Rate limit",
        Order = 1)]
    [DefaultValue(300)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "rate-limit", Order = 1)]
    public int RequestsPerMinute { get; set; } = 300;

    /// <summary>Window size in seconds used for request counting.</summary>
    [Display(
        Name = "Window seconds",
        Description = "Window size in seconds used for request counting.",
        GroupName = "Rate limit",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "rate-limit", Order = 2)]
    public int WindowSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum number of distinct client windows retained in memory. Bounds the per-client
    /// tracking dictionary so a flood of distinct client keys cannot drive the gateway to
    /// memory exhaustion (a DoS against the DoS-protection itself). When the cap is reached,
    /// stale entries are pruned first, then a window that is not actively rate-limiting a
    /// client is evicted; if none can be freed, the new request is rejected with 429 rather
    /// than inserting. Windows actively counting toward a 429 are never evicted, so a flood
    /// cannot clear an attacker's own throttle. A non-positive value disables the cap.
    /// </summary>
    [Display(
        Name = "Max entries",
        Description = "Maximum number of distinct client windows retained in memory. Bounds the per-client tracking dictionary so a flood of distinct client keys cannot drive the gateway to memory exhaustion (a DoS against the DoS-protection itself). When the cap is reached, stale entries are pruned first, then a window that is not actively rate-limiting a client is evicted; if none can be freed, the new request is rejected with 429 rather than inserting. Windows actively counting toward a 429 are never evicted, so a flood cannot clear an attacker's own throttle. A non-positive value disables the cap.",
        GroupName = "Rate limit",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "rate-limit", Order = 3)]
    public int MaxEntries { get; set; } = 10_000;
}

/// <summary>
/// Explicit SignalR hub transport limits. When this section is absent, secure defaults are
/// applied (see <c>SignalRHubLimits</c>) rather than the framework's implicit values, so the
/// gateway always bounds inbound frame size and per-connection concurrency intentionally.
/// </summary>
public sealed class SignalRConfig
{
    /// <summary>
    /// Maximum size, in bytes, of a single inbound hub message. Must accommodate base64-encoded
    /// inline media (which exceeds the framework's 32 KB default) while bounding runaway frames.
    /// Non-positive values fall back to the secure default.
    /// </summary>
    [Display(
        Name = "Maximum receive message size bytes",
        Description = "Maximum size, in bytes, of a single inbound hub message. Must accommodate base64-encoded inline media (which exceeds the framework's 32 KB default) while bounding runaway frames. Non-positive values fall back to the secure default.",
        GroupName = "Signal r",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "signal-r", Order = 0)]
    public long? MaximumReceiveMessageSizeBytes { get; set; }

    /// <summary>
    /// Maximum number of hub method invocations a single connection may run in parallel.
    /// Bounds concurrent work a client can force on the server. Non-positive values fall back to
    /// the secure default.
    /// </summary>
    [Display(
        Name = "Maximum parallel invocations per client",
        Description = "Maximum number of hub method invocations a single connection may run in parallel. Bounds concurrent work a client can force on the server. Non-positive values fall back to the secure default.",
        GroupName = "Signal r",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "signal-r", Order = 1)]
    public int? MaximumParallelInvocationsPerClient { get; set; }

    /// <summary>
    /// Maximum number of items buffered for client upload streams before processing blocks.
    /// Non-positive values fall back to the secure default.
    /// </summary>
    [Display(
        Name = "Stream buffer capacity",
        Description = "Maximum number of items buffered for client upload streams before processing blocks. Non-positive values fall back to the secure default.",
        GroupName = "Signal r",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "signal-r", Order = 2)]
    public int? StreamBufferCapacity { get; set; }

    /// <summary>
    /// Interval, in seconds, at which the server sends keep-alive pings to idle clients (#1840).
    /// Chosen to sit comfortably under the netbird tunnel idle-cutoff so a quiet mobile connection
    /// never idles the tunnel out mid-session. Non-positive values fall back to the mobile-tuned
    /// default. The server timeout (<see cref="ClientTimeoutIntervalSeconds"/>) is always coerced
    /// to at least twice this value so a single dropped ping cannot terminate the connection.
    /// </summary>
    [Display(
        Name = "Keep alive interval seconds",
        Description = "Interval, in seconds, at which the server sends keep-alive pings to idle clients (#1840). Chosen to sit comfortably under the netbird tunnel idle-cutoff so a quiet mobile connection never idles the tunnel out mid-session. Non-positive values fall back to the mobile-tuned default. The server timeout (ClientTimeoutIntervalSeconds) is always coerced to at least twice this value so a single dropped ping cannot terminate the connection.",
        GroupName = "Signal r",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "signal-r", Order = 3)]
    public int? KeepAliveIntervalSeconds { get; set; }

    /// <summary>
    /// Interval, in seconds, after which the server considers a client dead if no message or ping
    /// has arrived (#1840). Widened over the framework's 30s default to tolerate the jitter and
    /// brief stalls of a mobile link tunnelled through netbird. Must be at least twice
    /// <see cref="KeepAliveIntervalSeconds"/>; smaller (or non-positive) values are coerced up to
    /// the mobile-tuned default so a misconfig cannot make the server hang up prematurely.
    /// </summary>
    [Display(
        Name = "Client timeout interval seconds",
        Description = "Interval, in seconds, after which the server considers a client dead if no message or ping has arrived (#1840). Widened over the framework's 30s default to tolerate the jitter and brief stalls of a mobile link tunnelled through netbird. Must be at least twice KeepAliveIntervalSeconds; smaller (or non-positive) values are coerced up to the mobile-tuned default so a misconfig cannot make the server hang up prematurely.",
        GroupName = "Signal r",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "signal-r", Order = 4)]
    public int? ClientTimeoutIntervalSeconds { get; set; }
}

/// <summary>
/// Operator-supplied additional secret redaction patterns (#2727).
/// </summary>
/// <remarks>
/// Patterns here are applied <b>in addition to</b> the platform's built-in credential regexes and can
/// never replace or disable them, so a deployment can teach the redactor its own secret shapes
/// (internal service tokens, customer identifiers, bespoke API key formats) without a code change.
/// Every pattern is validated at startup: a malformed or all-matching pattern is a configuration
/// error naming the offending entry, because silently disabling redaction is the one outcome worse
/// than not supporting custom patterns at all.
/// </remarks>
public sealed class SecretRedactionConfig
{
    /// <summary>
    /// Additional .NET regular expressions whose matches are replaced with <c>[REDACTED]</c>.
    /// Applied after the built-in pattern set. Empty or absent means "built-ins only".
    /// </summary>
    [Display(
        Name = "Additional redaction patterns",
        Description = "Extra .NET regular expressions whose matches are replaced with [REDACTED]. Applied in addition to the built-in credential patterns, never instead of them.",
        GroupName = "Gateway",
        Order = 10)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "gateway", Order = 10)]
    public List<string>? Patterns { get; set; }

    /// <summary>
    /// Per-pattern match timeout in milliseconds applied to operator patterns so a
    /// catastrophic-backtracking expression cannot hang the logging path. Defaults to 100ms.
    /// </summary>
    [Display(
        Name = "Redaction match timeout (ms)",
        Description = "Per-pattern match timeout for operator redaction patterns. Defaults to 100ms.",
        GroupName = "Gateway",
        Order = 11)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "gateway", Order = 11)]
    public int? MatchTimeoutMilliseconds { get; set; }
}

/// <summary>Cron scheduler configuration.</summary>
public sealed class CronConfig
{
    /// <summary>Whether the cron scheduler is enabled.</summary>
    [Display(
        Name = "Enable cron",
        Description = "Whether the cron scheduler runs scheduled jobs.",
        GroupName = "Cron",
        Order = 0)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "cron", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>Scheduler polling interval in seconds.</summary>
    [Display(
        Name = "Tick interval (seconds)",
        Description = "How often the scheduler wakes to evaluate due jobs, in seconds.",
        GroupName = "Cron",
        Order = 1)]
    [DefaultValue(60)]
    [Range(1, int.MaxValue)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "cron", Order = 1)]
    public int TickIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// #3779: hostnames refused as cron webhook targets, on top of the address classes the shared
    /// SSRF policy blocks structurally (loopback, RFC-1918, link-local, cloud metadata).
    /// </summary>
    /// <remarks>
    /// Exists for the case the address table structurally cannot catch: an internal service on a
    /// publicly-resolving hostname. Matched exactly and case-insensitively against the URL host.
    /// Empty means no configured blocks, which is the pre-#3779 behaviour exactly.
    /// </remarks>
    [Display(
        Name = "Webhook blocked hosts",
        Description = "Hostnames refused as cron webhook targets, in addition to the always-blocked loopback, private, link-local and cloud-metadata address ranges. Use for internal services on publicly-resolving names. Exact, case-insensitive host match.",
        GroupName = "Cron",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cron", Order = 3)]
    public List<string>? WebhookBlockedHosts { get; set; }

    /// <summary>Optional job definitions keyed by stable job ID.</summary>
    [Display(
        Name = "Jobs",
        Description = "Optional job definitions keyed by stable job ID.",
        GroupName = "Cron",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cron", Order = 2)]
    public Dictionary<string, CronJobConfig>? Jobs { get; set; }
}

/// <summary>Config-defined cron job descriptor.</summary>
public sealed class CronJobConfig
{
    /// <summary>Display name for the cron job.</summary>
    [Display(
        Name = "Job name",
        Description = "Human-readable name shown for this job in schedules and run reports.",
        GroupName = "Cron job",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cron-job", Order = 0)]
    public string? Name { get; set; }

    /// <summary>Cron expression schedule.</summary>
    [Display(
        Name = "Schedule",
        Description = "Five-field cron expression (minute hour day month weekday) controlling when the job fires.",
        GroupName = "Cron job",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cron-job", Order = 1)]
    public string? Schedule { get; set; }

    /// <summary>Action type (for example: <c>agent-prompt</c>).</summary>
    [Display(
        Name = "Action type",
        Description = "What the job does when it fires: agent-prompt sends a prompt to an agent; command runs a shell command.",
        GroupName = "Cron job",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "cron-job", Order = 2)]
    public string? ActionType { get; set; }

    /// <summary>Target agent identifier for agent prompt jobs.</summary>
    [Display(
        Name = "Target agent",
        Description = "Agent that receives the prompt. Required for agent-prompt jobs.",
        GroupName = "Cron job",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "cron-job", Order = 3)]
    public string? AgentId { get; set; }

    /// <summary>Prompt message for agent prompt jobs.</summary>
    [Display(
        Name = "Prompt message",
        Description = "Prompt text sent to the agent. Required for agent-prompt jobs unless a template is named.",
        GroupName = "Cron job",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cron-job", Order = 4)]
    public string? Message { get; set; }

    /// <summary>Named prompt template for agent prompt jobs.</summary>
    [Display(
        Name = "Prompt template",
        Description = "Named prompt template rendered instead of a literal message.",
        GroupName = "Cron job",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "cron-job", Order = 5)]
    public string? TemplateName { get; set; }

    /// <summary>Template parameter values used when rendering <see cref="TemplateName" />.</summary>
    [Display(
        Name = "Template parameters",
        Description = "Values substituted into the named prompt template's placeholders.",
        GroupName = "Cron job",
        Order = 6)]
    [ConfigField(Group = "cron-job", Order = 6)]
    public Dictionary<string, string>? TemplateParameters { get; set; }

    /// <summary>Optional model override for agent prompt jobs.</summary>
    [Display(
        Name = "Model override",
        Description = "Model this job runs on, overriding the agent's configured model.",
        GroupName = "Cron job",
        Order = 7)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "cron-job", Order = 7, OptionsSource = "models")]
    public string? Model { get; set; }

    /// <summary>Webhook destination URL for webhook jobs.</summary>
    [Display(
        Name = "Webhook URL",
        Description = "Destination URL called when the job fires. Used by webhook jobs.",
        GroupName = "Cron job",
        Order = 8)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cron-job", Order = 8)]
    public string? WebhookUrl { get; set; }

    /// <summary>Shell command payload for shell jobs.</summary>
    /// <remarks>
    /// This is an arbitrary-execution surface: whatever is stored here runs with the gateway's
    /// privileges on every fire. Treat creating or editing it as a dangerous operation.
    /// </remarks>
    [Display(
        Name = "Shell command",
        Description = "Command or script executed when the job fires. Runs with the gateway's privileges - treat as a dangerous operation.",
        GroupName = "Cron job",
        Order = 9)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cron-job", Order = 9)]
    public string? ShellCommand { get; set; }

    /// <summary>Whether this job is enabled.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether this job fires on its schedule. A disabled job stays defined but never runs.",
        GroupName = "Cron job",
        Order = 10)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "cron-job", Order = 10)]
    public bool Enabled { get; set; } = true;

    /// <summary>Optional creator label for auditing.</summary>
    [Display(
        Name = "Created by",
        Description = "Label recording who or what created this job. Used for auditing only.",
        GroupName = "Cron job",
        Order = 11)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "cron-job", Order = 11)]
    public string? CreatedBy { get; set; }

    /// <summary>Optional metadata entries persisted with the job.</summary>
    [Display(
        Name = "Metadata",
        Description = "Free-form key/value entries persisted alongside the job, such as a timeout override.",
        GroupName = "Cron job",
        Order = 12)]
    [ConfigField(Group = "cron-job", Order = 12)]
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>Named prompt template descriptor.</summary>
public sealed class PromptTemplateConfig
{
    /// <summary>Template body with <c>{{parameter}}</c> placeholders.</summary>
    [Display(
        Name = "Prompt",
        Description = "Template body with {{parameter}} placeholders.",
        GroupName = "Prompt template",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "prompt-template", Order = 0)]
    public string? Prompt { get; set; }

    /// <summary>Optional human-friendly description.</summary>
    [Display(
        Name = "Description",
        Description = "Optional human-friendly description.",
        GroupName = "Prompt template",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "prompt-template", Order = 1)]
    public string? Description { get; set; }

    /// <summary>Default values for template parameters.</summary>
    [Display(
        Name = "Defaults",
        Description = "Default values for template parameters.",
        GroupName = "Prompt template",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "prompt-template", Order = 2)]
    public Dictionary<string, string>? Defaults { get; set; }

    /// <summary>Optional per-parameter metadata and defaults.</summary>
    [Display(
        Name = "Parameters",
        Description = "Optional per-parameter metadata and defaults.",
        GroupName = "Prompt template",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "prompt-template", Order = 3)]
    public Dictionary<string, PromptTemplateParameterConfig>? Parameters { get; set; }
}

/// <summary>Prompt template parameter configuration.</summary>
public sealed class PromptTemplateParameterConfig
{
    /// <summary>Optional parameter description.</summary>
    [Display(
        Name = "Description",
        Description = "Optional parameter description.",
        GroupName = "Prompt template parameter",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "prompt-template-parameter", Order = 0)]
    public string? Description { get; set; }

    /// <summary>Optional default value.</summary>
    [Display(
        Name = "Default",
        Description = "Optional default value.",
        GroupName = "Prompt template parameter",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "prompt-template-parameter", Order = 1)]
    public string? Default { get; set; }

    /// <summary>Whether the parameter must be supplied if no default exists.</summary>
    [Display(
        Name = "Required",
        Description = "Whether the parameter must be supplied if no default exists.",
        GroupName = "Prompt template parameter",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "prompt-template-parameter", Order = 2)]
    public bool Required { get; set; }
}

/// <summary>Configuration for dynamic extension discovery and loading.</summary>
public sealed class ExtensionsConfig
{
    /// <summary>
    /// Root directory containing extension folders with botnexus-extension.json manifests.
    /// </summary>
    [Display(
        Name = "Path",
        Description = "Root directory containing extension folders with botnexus-extension.json manifests.",
        GroupName = "Extensions",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "extensions", Order = 0)]
    public string? Path { get; set; }

    /// <summary>
    /// Enables or disables dynamic extension loading.
    /// </summary>
    [Display(
        Name = "Enabled",
        Description = "Enables or disables dynamic extension loading.",
        GroupName = "Extensions",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "extensions", Order = 1)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// World-level default extension configuration, keyed by extension ID.
    /// Deep-merged with agent-level overrides to produce effective config per agent.
    /// </summary>
    [Display(
        Name = "Defaults",
        Description = "World-level default extension configuration, keyed by extension ID. Deep-merged with agent-level overrides to produce effective config per agent.",
        GroupName = "Extensions",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "extensions", Order = 2)]
    public Dictionary<string, JsonElement>? Defaults { get; set; }
}

/// <summary>Agent definition in platform config.</summary>
public sealed class AgentDefinitionConfig
{
    /// <summary>Provider name (e.g. 'copilot').</summary>
    [Display(
        Name = "Provider",
        Description = "Provider name (e.g. 'copilot').",
        GroupName = "Agent",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 6)]
    public string? Provider { get; set; }
    /// <summary>Human-readable display name.</summary>
    [Display(
        Name = "Display name",
        Description = "Human-readable display name shown for this agent in clients.",
        GroupName = "Agent",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 1)]
    public string? DisplayName { get; set; }
    /// <summary>Optional emoji shown alongside the agent name in clients.</summary>
    [Display(
        Name = "Emoji",
        Description = "Optional emoji shown alongside the agent name in clients.",
        GroupName = "Agent",
        Order = 7)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 7)]
    public string? Emoji { get; set; }
    /// <summary>Description of the agent's purpose.</summary>
    [Display(
        Name = "Description",
        Description = "Description of the agent's purpose.",
        GroupName = "Agent",
        Order = 8)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 8)]
    public string? Description { get; set; }

    /// <summary>Agent-maintained summary of what the agent is currently doing (#3596).</summary>
    /// <remarks>
    /// Written by the agent itself through <c>update_agent</c>, not by a human editing config.
    /// It is persisted here so it survives a gateway restart on the same path as every other
    /// descriptor mutation, rather than needing a second store.
    /// </remarks>
    [Display(
        Name = "Summary",
        Description = "Agent-maintained summary of what this agent is currently doing. Written by the agent itself; the static description stays human-owned.",
        GroupName = "Agent",
        Order = 32)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 32)]
    public string? Summary { get; set; }
    /// <summary>Model identifier (e.g. 'gpt-4.1').</summary>
    [Display(
        Name = "Model",
        Description = "Model identifier this agent uses (for example gpt-4.1).",
        GroupName = "Agent",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 2)]
    public string? Model { get; set; }
    /// <summary>Model IDs this agent is allowed to use. Null means unrestricted within provider allowlist.</summary>
    [Display(
        Name = "Allowed models",
        Description = "Model IDs this agent is allowed to use. Null means unrestricted within provider allowlist.",
        GroupName = "Agent",
        Order = 9)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 9)]
    public List<string>? AllowedModels { get; set; }
    /// <summary>Ordered list of files to load as the system prompt. Empty = default order.</summary>
    [Display(
        Name = "System prompt files",
        Description = "Ordered list of files to load as the system prompt. Empty = default order.",
        GroupName = "Agent",
        Order = 10)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 10)]
    public List<string>? SystemPromptFiles { get; set; }
    /// <summary>Path to a single system prompt file (legacy, prefer SystemPromptFiles).</summary>
    [Display(
        Name = "System prompt file",
        Description = "Path to a single system prompt file (legacy, prefer SystemPromptFiles).",
        GroupName = "Agent",
        Order = 11)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 11)]
    public string? SystemPromptFile { get; set; }
    /// <summary>Tool identifiers this agent has access to.</summary>
    [Display(
        Name = "Tool ids",
        Description = "Tool identifiers this agent has access to.",
        GroupName = "Agent",
        Order = 12)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 12)]
    public List<string>? ToolIds { get; set; }
    /// <summary>Per-tool timeout in seconds for runtime tool execution safety caps.</summary>
    [Display(
        Name = "Tool timeout seconds",
        Description = "Per-tool timeout in seconds for runtime tool execution safety caps.",
        GroupName = "Agent",
        Order = 13)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "agent", Order = 13)]
    public int? ToolTimeoutSeconds { get; set; }
    /// <summary>Agent IDs this agent can call as sub-agents.</summary>
    [Display(
        Name = "Sub agents",
        Description = "Agent IDs this agent can call as sub-agents.",
        GroupName = "Agent",
        Order = 14)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 14)]
    public List<string>? SubAgents { get; set; }
    /// <summary>Role names this agent can converse with (role-based grants for agent_converse).</summary>
    [Display(
        Name = "Sub agent roles",
        Description = "Role names this agent can converse with (role-based grants for agent_converse).",
        GroupName = "Agent",
        Order = 15)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 15)]
    public List<string>? SubAgentRoles { get; set; }
    /// <summary>Isolation strategy name (e.g. 'in-process').</summary>
    [Display(
        Name = "Isolation strategy",
        Description = "Isolation strategy name (e.g. 'in-process').",
        GroupName = "Agent",
        Order = 16)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 16)]
    public string? IsolationStrategy { get; set; }
    /// <summary>Prompt caching retention policy for this agent. Null means provider default (short) is used.</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<BotNexus.Agent.Providers.Core.Models.CacheRetention>))]
    [Display(
        Name = "Cache retention",
        Description = "Prompt caching retention policy for this agent. Null means provider default (short) is used.",
        GroupName = "Agent",
        Order = 17)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 17)]
    public BotNexus.Agent.Providers.Core.Models.CacheRetention? CacheRetention { get; set; }
    /// <summary>
    /// Agent-level default thinking (reasoning) level. Agent layer of the three-layer
    /// model/thinking/context override stack consumed by <c>ModelOverrideResolver</c>.
    /// Null means "unset - inherit the model default". Validated against the selected
    /// model's advertised capabilities when the descriptor is built; an unsupported value
    /// causes the agent to be skipped at config load with a warning.
    /// </summary>
    [Display(
        Name = "Thinking level",
        Description = "Default reasoning effort this agent requests (validated against the model's capabilities).",
        GroupName = "Agent",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 4)]
    public string? Thinking { get; set; }
    /// <summary>
    /// Agent-level default context-window size in tokens. Agent layer of the three-layer
    /// override stack. Null means "unset - inherit the model default". Validated against the
    /// selected model's advertised context sizes; an unsupported value causes the agent to be
    /// skipped at config load with a warning.
    /// </summary>
    [Display(
        Name = "Context window",
        Description = "Default context-window size (tokens) this agent requests (validated against the model's capabilities).",
        GroupName = "Agent",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 5)]
    public int? ContextWindow { get; set; }
    /// <summary>Maximum concurrent sessions for this agent.</summary>
    [Display(
        Name = "Max concurrent sessions",
        Description = "Maximum concurrent sessions for this agent.",
        GroupName = "Agent",
        Order = 18)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "agent", Order = 18)]
    public int? MaxConcurrentSessions { get; set; }
    /// <summary>Agent-level metadata.</summary>
    [Display(
        Name = "Metadata",
        Description = "Agent-level metadata.",
        GroupName = "Agent",
        Order = 19)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 19)]
    public JsonElement? Metadata { get; set; }
    /// <summary>Strategy-specific isolation options.</summary>
    [Display(
        Name = "Isolation options",
        Description = "Strategy-specific isolation options.",
        GroupName = "Agent",
        Order = 20)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 20)]
    public JsonElement? IsolationOptions { get; set; }
    /// <summary>Whether this agent is enabled.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether this agent is enabled and available for routing.",
        GroupName = "Agent",
        Order = 3)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "agent", Order = 3)]
    public bool Enabled { get; set; } = true;
    /// <summary>Memory system configuration for this agent.</summary>
    [Display(
        Name = "Memory",
        Description = "Memory system configuration for this agent.",
        GroupName = "Agent",
        Order = 21)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 21)]
    public MemoryAgentConfig? Memory { get; set; }
    /// <summary>Soul session lifecycle configuration for this agent.</summary>
    [Display(
        Name = "Soul",
        Description = "Soul session lifecycle configuration for this agent.",
        GroupName = "Agent",
        Order = 22)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 22)]
    public SoulAgentConfig? Soul { get; set; }
    /// <summary>Heartbeat polling configuration.</summary>
    [Display(
        Name = "Heartbeat",
        Description = "Heartbeat polling configuration.",
        GroupName = "Agent",
        Order = 23)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 23)]
    public HeartbeatAgentConfig? Heartbeat { get; set; }
    /// <summary>Datetime injection configuration override for this agent. Overrides world default when set.</summary>
    [Display(
        Name = "Date time injection",
        Description = "Datetime injection configuration override for this agent. Overrides world default when set.",
        GroupName = "Agent",
        Order = 24)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 24)]
    public DateTimeInjectionConfig? DateTimeInjection { get; set; }
    /// <summary>Session access configuration for this agent's session tool.</summary>
    [Display(
        Name = "Session access",
        Description = "Session access configuration for this agent's session tool.",
        GroupName = "Agent",
        Order = 25)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 25)]
    public SessionAccessConfig? SessionAccess { get; set; }
    /// <summary>Conversation access configuration for this agent's conversation tool.</summary>
    [Display(
        Name = "Conversation access",
        Description = "Conversation access configuration for this agent's conversation tool.",
        GroupName = "Agent",
        Order = 26)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 26)]
    public ConversationAccessConfig? ConversationAccess { get; set; }
    /// <summary>File access policy for this agent's file tools.</summary>
    [Display(
        Name = "File access",
        Description = "File access policy for this agent's file tools.",
        GroupName = "Agent",
        Order = 27)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 27)]
    public FileAccessPolicyConfig? FileAccess { get; set; }

    /// <summary>
    /// Custom shell command array for this agent. Overrides the gateway-level ShellCommand.
    /// Element [0] is the executable, remaining elements are base arguments.
    /// The agent's command string is appended as the final argument.
    /// </summary>
    [Display(
        Name = "Shell command",
        Description = "Custom shell command array for this agent. Overrides the gateway-level ShellCommand. Element [0] is the executable, remaining elements are base arguments. The agent's command string is appended as the final argument.",
        GroupName = "Agent",
        Order = 28)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 28)]
    public string[]? ShellCommand { get; set; }

    /// <summary>
    /// Extension-specific configuration keyed by extension ID.
    /// Each extension reads its own section (e.g., "botnexus-skills", "botnexus-exec").
    /// </summary>
    [Display(
        Name = "Extensions",
        Description = "Extension-specific configuration keyed by extension ID. Each extension reads its own section (e.g., \"botnexus-skills\", \"botnexus-exec\").",
        GroupName = "Agent",
        Order = 29)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "agent", Order = 29)]
    public Dictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Tool policy overrides for this agent.</summary>
    [Display(
        Name = "Tool policy",
        Description = "Tool policy overrides for this agent.",
        GroupName = "Agent",
        Order = 30)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 30)]
    public ToolPolicyConfig? ToolPolicy { get; set; }

    /// <summary>
    /// Optional. Kind of agent — currently only <c>Named</c> is accepted from config.
    /// <c>SubAgent</c> is rejected by <c>AgentDescriptorValidator.ValidateForConfig</c>;
    /// sub-agents are runtime-only and produced exclusively by
    /// <c>DefaultSubAgentManager.SpawnAsync</c>. Omit the field entirely on existing
    /// configs; the default is <c>Named</c>.
    /// </summary>
    [Display(
        Name = "Kind",
        Description = "Optional. Kind of agent — currently only Named is accepted from config. SubAgent is rejected by AgentDescriptorValidator.ValidateForConfig; sub-agents are runtime-only and produced exclusively by DefaultSubAgentManager.SpawnAsync. Omit the field entirely on existing configs; the default is Named.",
        GroupName = "Agent",
        Order = 31)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "agent", Order = 31)]
    public AgentKind? Kind { get; set; }
}

/// <summary>Per-agent file access policy configuration.</summary>
public sealed class FileAccessPolicyConfig
{
    /// <summary>Paths the agent can read (exact paths or glob patterns).</summary>
    [Display(
        Name = "Allowed read paths",
        Description = "Paths the agent can read (exact paths or glob patterns).",
        GroupName = "File access policy",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "file-access-policy", Order = 0)]
    public List<string>? AllowedReadPaths { get; set; }

    /// <summary>Paths the agent can write (exact paths or glob patterns).</summary>
    [Display(
        Name = "Allowed write paths",
        Description = "Paths the agent can write (exact paths or glob patterns).",
        GroupName = "File access policy",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "file-access-policy", Order = 1)]
    public List<string>? AllowedWritePaths { get; set; }

    /// <summary>Paths explicitly denied even if otherwise allowed.</summary>
    [Display(
        Name = "Denied paths",
        Description = "Paths explicitly denied even if otherwise allowed.",
        GroupName = "File access policy",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "file-access-policy", Order = 2)]
    public List<string>? DeniedPaths { get; set; }
}

/// <summary>Per-agent tool policy configuration that overrides default risk classifications.</summary>
public sealed class ToolPolicyConfig
{
    /// <summary>Tools that always require approval regardless of default classification.</summary>
    [Display(
        Name = "Always approve",
        Description = "Tools that always require approval regardless of default classification.",
        GroupName = "Tool policy",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "tool-policy", Order = 0)]
    public List<string>? AlwaysApprove { get; set; }

    /// <summary>Tools that skip approval even if classified as dangerous (trusted).</summary>
    [Display(
        Name = "Never approve",
        Description = "Tools that skip approval even if classified as dangerous (trusted).",
        GroupName = "Tool policy",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "tool-policy", Order = 1)]
    public List<string>? NeverApprove { get; set; }

    /// <summary>Tools completely blocked for this agent.</summary>
    [Display(
        Name = "Denied",
        Description = "Tools completely blocked for this agent.",
        GroupName = "Tool policy",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "tool-policy", Order = 2)]
    public List<string>? Denied { get; set; }

    /// <summary>
    /// Posture applied when a tool requires approval but no approval workflow can service the
    /// request (issue #2391). Accepted values are <c>allow</c> (default, historical behaviour --
    /// execution proceeds with an audit record) and <c>deny</c> (fail closed -- the call is
    /// refused with an <c>ask-fallback-deny</c> reason).
    /// </summary>
    /// <remarks>
    /// Leave unset for unattended agents, cron jobs, and sub-agents: there is no interactive
    /// tool-approval workflow at the <c>BeforeToolCall</c> seam, so <c>deny</c> makes every
    /// approval-required tool unusable for that agent. Set it deliberately for agents whose
    /// dangerous tools should never run without a human in the loop.
    /// </remarks>
    [Display(
        Name = "Ask fallback",
        Description = "Posture applied when a tool requires approval but no approval workflow can service the request (issue #2391). Accepted values are allow (default, historical behaviour -- execution proceeds with an audit record) and deny (fail closed -- the call is refused with an ask-fallback-deny reason).",
        GroupName = "Tool policy",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "tool-policy", Order = 3)]
    public string? AskFallback { get; set; }

    /// <summary>
    /// Tools exempted from <see cref="AskFallback"/> when it is <c>deny</c>. An approval-required
    /// tool named here falls back to <c>allow</c> instead of being refused, so a fail-closed agent
    /// can still keep a narrow set of tools working.
    /// </summary>
    [Display(
        Name = "Ask fallback allow",
        Description = "Tools exempted from AskFallback when it is deny. An approval-required tool named here falls back to allow instead of being refused, so a fail-closed agent can still keep a narrow set of tools working.",
        GroupName = "Tool policy",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "tool-policy", Order = 4)]
    public List<string>? AskFallbackAllow { get; set; }
}

/// <summary>Controls what sessions an agent can access via the session tool.</summary>
public sealed class SessionAccessConfig
{
    /// <summary>Access level: "own" (default), "allowlist", or "all".</summary>
    [Display(
        Name = "Level",
        Description = "Access level: \"own\" (default), \"allowlist\", or \"all\".",
        GroupName = "Session access",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-access", Order = 0)]
    public string Level { get; set; } = "own";
    /// <summary>Agent IDs this agent can view sessions for (when level is "allowlist").</summary>
    [Display(
        Name = "Allowed agents",
        Description = "Agent IDs this agent can view sessions for (when level is \"allowlist\").",
        GroupName = "Session access",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-access", Order = 1)]
    public List<string>? AllowedAgents { get; set; }
}

/// <summary>Controls what conversations an agent can access via the conversation tool.</summary>
public sealed class ConversationAccessConfig
{
    /// <summary>Access level: "own" (default), "allowlist", or "all".</summary>
    [Display(
        Name = "Level",
        Description = "Access level: \"own\" (default), \"allowlist\", or \"all\".",
        GroupName = "Conversation access",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "conversation-access", Order = 0)]
    public string Level { get; set; } = "own";
    /// <summary>Agent IDs this agent can view conversations for (when level is "allowlist").</summary>
    [Display(
        Name = "Allowed agents",
        Description = "Agent IDs this agent can view conversations for (when level is \"allowlist\").",
        GroupName = "Conversation access",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "conversation-access", Order = 1)]
    public List<string>? AllowedAgents { get; set; }
}

/// <summary>Channel definition in platform config.</summary>
/// <remarks>
/// <para><b>Round-trip safety (#2816).</b> On 2026-07-31 a production write reduced an entire
/// populated <c>channels</c> section to <c>{"enabled": true}</c>, destroying a Service Bus
/// connection block and two Telegram bot tokens. The mechanism was this class: it modelled only
/// <see cref="Type"/>, <see cref="Enabled"/> (which defaults to <see langword="true"/>) and a
/// flat <c>Dictionary&lt;string,string&gt;</c> of settings, so serialising the typed graph back
/// over the document emitted exactly the one defaulted property and silently dropped everything
/// else the operator had written.</para>
/// <para>Real channel blocks are richer than that by design - <c>telegram.bots.&lt;name&gt;.token</c>,
/// <c>serviceBus.queues.&lt;name&gt;.maxConcurrent</c> and every other adapter-owned subtree are
/// nested and non-string, and the platform deliberately does not model adapter options centrally.
/// So the fix is not to enumerate them here (which would go stale the moment a new adapter ships)
/// but to <em>carry them through</em>: <see cref="AdditionalSettings"/> is a
/// <see cref="JsonExtensionDataAttribute"/> overflow member that captures every property this
/// class does not name and re-emits it verbatim on serialisation.</para>
/// <para>Consequently this member must not be removed, renamed away from its overflow role, or
/// "tidied" for being untyped. Removing it re-opens a silent credential-destruction path; see the
/// destructive-write guard in <see cref="PlatformConfigWriter"/>, which is the second, independent
/// line of defence for the same defect.</para>
/// </remarks>
public sealed class ChannelConfig
{
    /// <summary>Channel type (e.g. 'signalr', 'slack').</summary>
    [Display(
        Name = "Type",
        Description = "Channel type (e.g. 'signalr', 'slack').",
        GroupName = "Channel",
        Order = 100)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "channel", Order = 100)]
    public string? Type { get; set; }
    /// <summary>Whether this channel is enabled.</summary>
    [Display(
        Name = "Enabled",
        Description = "Whether this channel is enabled.",
        GroupName = "Channel",
        Order = 1)]
    [DefaultValue(true)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "channel", Order = 1)]
    public bool Enabled { get; set; } = true;
    /// <summary>Adapter-specific settings.</summary>
    /// <remarks>
    /// #2816: typed as <see cref="JsonElement"/> values rather than <see cref="string"/> because a
    /// real adapter setting is routinely an object, array, number or bool. With the previous
    /// <c>Dictionary&lt;string,string&gt;</c> such a value could not even be represented, so it was
    /// lost on the way through the typed graph.
    /// </remarks>
    [Display(
        Name = "Settings",
        Description = "Adapter-specific settings.",
        GroupName = "Channel",
        Order = 101)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "channel", Order = 101)]
    public Dictionary<string, JsonElement>? Settings { get; set; }

    /// <summary>
    /// Every channel property this class does not model, captured verbatim so a typed round-trip
    /// through <see cref="PlatformConfig"/> cannot destroy adapter-owned configuration (#2816).
    /// </summary>
    /// <remarks>
    /// This is load-bearing, not incidental: it is what stops <c>channels.telegram.bots</c> and
    /// <c>channels.serviceBus.queues</c> - live credentials and routing - from being erased by a
    /// write that was aimed at an entirely different section. Do not remove it. Do not replace it
    /// with an enumerated set of adapter properties; adapters are extensions and the platform
    /// cannot know their shapes in advance.
    /// <para>Deliberately annotated as a <see cref="ConfigFieldWidget.Text"/> passthrough rather
    /// than left bare: the <c>[ConfigField]</c> coverage fence (#2701) requires every settable
    /// property reachable from <see cref="PlatformConfig"/> to declare itself, and silencing it via
    /// the fence baseline was not an option - the baseline may only shrink, and rightly so.</para>
    /// </remarks>
    [JsonExtensionData]
    [Display(
        Name = "Additional settings",
        Description = "Channel-specific settings not modelled elsewhere. Preserved verbatim on write so unknown keys are never dropped (#2816).")]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "channel", Order = 99)]
    public Dictionary<string, JsonElement>? AdditionalSettings { get; set; }
}

/// <summary>Session store implementation configuration.</summary>
public sealed class SessionStoreConfig
{
    /// <summary>Store type. Supported values: InMemory, File, or Sqlite.</summary>
    [Display(
        Name = "Type",
        Description = "Store type. Supported values: InMemory, File, or Sqlite.",
        GroupName = "Session store",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-store", Order = 3)]
    public string? Type { get; set; }
    /// <summary>Path used by file-based session store implementation.</summary>
    [Display(
        Name = "File path",
        Description = "Path used by file-based session store implementation.",
        GroupName = "Session store",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "session-store", Order = 4)]
    public string? FilePath { get; set; }
    /// <summary>Connection string used by SQLite session store implementation.</summary>
    [Display(
        Name = "Connection string",
        Description = "Connection string for the session store. Sensitive: contains credentials and is stored and shown masked.")]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "session-store", Order = 2, Secret = true)]
    public string? ConnectionString { get; set; }
}

/// <summary>API key entry used for multi-tenant gateway auth.</summary>
public sealed class ApiKeyConfig
{
    /// <summary>The raw API key value.</summary>
    [Display(
        Name = "API key",
        Description = "The raw API key value used for gateway authentication. Sensitive: stored and shown masked.",
        GroupName = "API key",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Secret, Group = "api-key", Order = 0, Secret = true)]
    public string? ApiKey { get; set; }
    /// <summary>Tenant identifier for multi-tenant isolation.</summary>
    [Display(
        Name = "Tenant ID",
        Description = "Tenant identifier for multi-tenant isolation.",
        GroupName = "API key",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "api-key", Order = 1)]
    public string? TenantId { get; set; }
    /// <summary>Caller identifier used in audit logs.</summary>
    [Display(
        Name = "Caller ID",
        Description = "Caller identifier used in audit logs.",
        GroupName = "API key",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "api-key", Order = 2)]
    public string? CallerId { get; set; }
    /// <summary>Human-readable name for this key.</summary>
    [Display(
        Name = "Display name",
        Description = "Human-readable name for this key.",
        GroupName = "API key",
        Order = 3)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "api-key", Order = 3)]
    public string? DisplayName { get; set; }
    /// <summary>Agent IDs this key is allowed to access. Empty means all.</summary>
    [Display(
        Name = "Allowed agents",
        Description = "Agent IDs this key is allowed to access. Empty means all.",
        GroupName = "API key",
        Order = 4)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "api-key", Order = 4)]
    public List<string>? AllowedAgents { get; set; }
    /// <summary>Permissions granted to this key (e.g. 'chat:send', 'sessions:read').</summary>
    [Display(
        Name = "Permissions",
        Description = "Permissions granted to this key (e.g. 'chat:send', 'sessions:read').",
        GroupName = "API key",
        Order = 5)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "api-key", Order = 5)]
    public List<string>? Permissions { get; set; }
    /// <summary>Whether this key has administrative privileges.</summary>
    [Display(
        Name = "Is admin",
        Description = "Whether this key has administrative privileges.",
        GroupName = "API key",
        Order = 6)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "api-key", Order = 6)]
    public bool IsAdmin { get; set; }
}

/// <summary>
/// Workspace and portal display settings.
/// Controls limits for report and file preview in the portal UI.
/// </summary>
public sealed class WorkspacePortalConfig
{
    /// <summary>
    /// Maximum number of bytes read from a report file for portal preview.
    /// Files larger than this are truncated server-side and flagged in the UI.
    /// Defaults to 524288 (512 KB). Set to 0 for no server-side limit.
    /// </summary>
    [Display(
        Name = "Max report file size bytes",
        Description = "Maximum number of bytes read from a report file for portal preview. Files larger than this are truncated server-side and flagged in the UI. Defaults to 524288 (512 KB). Set to 0 for no server-side limit.",
        GroupName = "Workspace portal",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "workspace-portal", Order = 0)]
    public int MaxReportFileSizeBytes { get; set; } = 512 * 1024;
}

/// <summary>
/// Auxiliary (cheap/fast) model configuration for background gateway tasks.
/// </summary>
public sealed class AuxiliaryConfig
{
    /// <summary>
    /// Conversation title generation settings. Hydrated by
    /// <c>AuxiliarySchemaContributor</c> as a nested object (<c>{ model, timeoutSeconds }</c>);
    /// this property must remain an object so the bound config matches the on-disk shape.
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(TitlingConfigJsonConverter))]
    [Display(
        Name = "Titling",
        Description = "Conversation title generation settings. Hydrated by AuxiliarySchemaContributor as a nested object ({ model, timeoutSeconds }); this property must remain an object so the bound config matches the on-disk shape.",
        GroupName = "Auxiliary",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Select, Group = "auxiliary", Order = 0)]
    public TitlingConfig? Titling { get; set; }

    /// <summary>
    /// Model ID to use for session compaction summarisation (cheap/fast auxiliary model).
    /// Supports any registered provider model ID (e.g. "gpt-4o-mini", "claude-haiku-3-5").
    /// When null or empty the primary <see cref="CompactionOptions.SummarizationModel"/> or
    /// the compactor's default waterfall is used.
    /// If the resolved auxiliary model has a smaller context window than the compaction
    /// threshold, a startup warning is emitted but the gateway continues to run.
    /// </summary>
    [Display(
        Name = "Compression",
        Description = "Model ID to use for session compaction summarisation (cheap/fast auxiliary model). Supports any registered provider model ID (e.g. \"gpt-4o-mini\", \"claude-haiku-3-5\"). When null or empty the primary SummarizationModel or the compactor's default waterfall is used. If the resolved auxiliary model has a smaller context window than the compaction threshold, a startup warning is emitted but the gateway continues to run.",
        GroupName = "Auxiliary",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "auxiliary", Order = 1)]
    public string? Compression { get; set; }
}

/// <summary>
/// Conversation auto-title generation settings under <c>gateway.auxiliary.titling</c>.
/// </summary>
public sealed class TitlingConfig
{
    /// <summary>
    /// Master switch for conversation auto-titling. When false the gateway never schedules a
    /// title-generation call and conversations keep their default title until a user or agent
    /// renames them. Defaults to true. Surfaced as config because the only prior way to disable
    /// auto-titling was to leave no models registered, which is a poor proxy for intent.
    /// </summary>
    [Display(
        Name = "Enabled",
        Description = "Master switch for conversation auto-titling. When false the gateway never schedules a title-generation call and conversations keep their default title until a user or agent renames them. Defaults to true. Surfaced as config because the only prior way to disable auto-titling was to leave no models registered, which is a poor proxy for intent.",
        GroupName = "Titling",
        Order = 0)]
    [ConfigField(Widget = ConfigFieldWidget.Toggle, Group = "titling", Order = 0)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Model ID to use for auto-generating conversation titles after the first user+assistant
    /// exchange. Supports any registered provider model ID (e.g. "gpt-4o-mini",
    /// "claude-haiku-3-5", "gemini-2.0-flash-lite").
    /// When null or empty the primary session model is used as fallback.
    /// </summary>
    [Display(
        Name = "Model",
        Description = "Model ID to use for auto-generating conversation titles after the first user+assistant exchange. Supports any registered provider model ID (e.g. \"gpt-4o-mini\", \"claude-haiku-3-5\", \"gemini-2.0-flash-lite\"). When null or empty the primary session model is used as fallback.",
        GroupName = "Titling",
        Order = 1)]
    [ConfigField(Widget = ConfigFieldWidget.Text, Group = "titling", Order = 1)]
    public string? Model { get; set; }

    /// <summary>
    /// Maximum time in seconds allowed for the best-effort title generation call before it is
    /// abandoned. Defaults to 30 seconds. A non-positive value falls back to the 30s default so a
    /// mis-set zero never produces a zero-timeout that cancels every call instantly.
    /// </summary>
    [Display(
        Name = "Timeout seconds",
        Description = "Maximum time in seconds allowed for the best-effort title generation call before it is abandoned. Defaults to 30 seconds. A non-positive value falls back to the 30s default so a mis-set zero never produces a zero-timeout that cancels every call instantly.",
        GroupName = "Titling",
        Order = 2)]
    [ConfigField(Widget = ConfigFieldWidget.Number, Group = "titling", Order = 2)]
    public int TimeoutSeconds { get; set; } = 30;
}
