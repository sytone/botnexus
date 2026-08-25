using System.Text.Json.Serialization;

namespace BotNexus.Agent.Providers.Anthropic;

/// <summary>
/// A page of the Anthropic <c>GET /v1/models</c> response.
/// </summary>
public sealed class AnthropicModelsResponse
{
    /// <summary>The models in this page, newest first.</summary>
    [JsonPropertyName("data")]
    public List<AnthropicModelInfo>? Data { get; set; }

    /// <summary>
    /// True when further pages exist. The caller continues by passing <see cref="LastId"/> as the
    /// <c>after_id</c> query parameter.
    /// </summary>
    [JsonPropertyName("has_more")]
    public bool HasMore { get; set; }

    /// <summary>The id of the last model in this page, used as the pagination cursor.</summary>
    [JsonPropertyName("last_id")]
    public string? LastId { get; set; }
}

/// <summary>
/// One model entry from the Anthropic models endpoint.
/// </summary>
/// <remarks>
/// Unlike the Copilot discovery payload, Anthropic states the token budgets as top-level fields
/// rather than inside a capability limits map, so <see cref="MaxInputTokens"/> and
/// <see cref="MaxTokens"/> map straight onto the corresponding <c>LlmModel</c> members.
/// </remarks>
public sealed class AnthropicModelInfo
{
    /// <summary>The model identifier used in a messages request, e.g. <c>claude-sonnet-4-5-20250929</c>.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>The human-readable name, e.g. "Claude Sonnet 4.5". Falls back to <see cref="Id"/>.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    /// <summary>The maximum prompt size in tokens, i.e. the context window.</summary>
    [JsonPropertyName("max_input_tokens")]
    public int? MaxInputTokens { get; set; }

    /// <summary>The maximum number of output tokens the model will generate.</summary>
    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    /// <summary>The advertised capability set, when present.</summary>
    [JsonPropertyName("capabilities")]
    public AnthropicModelCapabilities? Capabilities { get; set; }
}

/// <summary>
/// The subset of Anthropic's advertised capabilities that maps onto an <c>LlmModel</c>. Capabilities
/// the registry has no field for (batch, citations, pdf_input, structured_outputs) are deliberately
/// not modelled, so this type stays a projection rather than a mirror of the wire format.
/// </summary>
public sealed class AnthropicModelCapabilities
{
    /// <summary>Whether the model accepts image content blocks.</summary>
    [JsonPropertyName("image_input")]
    public AnthropicCapabilityFlag? ImageInput { get; set; }

    /// <summary>Whether the model supports extended thinking.</summary>
    [JsonPropertyName("thinking")]
    public AnthropicCapabilityFlag? Thinking { get; set; }

    /// <summary>The adaptive-effort tiers the model accepts.</summary>
    [JsonPropertyName("effort")]
    public AnthropicEffortCapability? Effort { get; set; }
}

/// <summary>
/// A capability node carrying nothing but its own <c>supported</c> flag.
/// </summary>
public class AnthropicCapabilityFlag
{
    /// <summary>Whether the capability is supported.</summary>
    [JsonPropertyName("supported")]
    public bool Supported { get; set; }
}

/// <summary>
/// The <c>effort</c> capability, whose nested tiers say which thinking budgets the model accepts.
/// </summary>
/// <remarks>
/// The presence of <c>xhigh</c> or <c>max</c> is what distinguishes a model that can take the
/// ExtraHigh thinking tier, so these two are read directly rather than inferred from the model name.
/// </remarks>
public sealed class AnthropicEffortCapability : AnthropicCapabilityFlag
{
    /// <summary>The extra-high effort tier.</summary>
    [JsonPropertyName("xhigh")]
    public AnthropicCapabilityFlag? XHigh { get; set; }

    /// <summary>The maximum effort tier.</summary>
    [JsonPropertyName("max")]
    public AnthropicCapabilityFlag? Max { get; set; }
}
