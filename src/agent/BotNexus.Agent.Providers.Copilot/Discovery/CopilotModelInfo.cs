using System.Text.Json.Serialization;

namespace BotNexus.Agent.Providers.Copilot.Discovery;

/// <summary>
/// Response shape of <c>GET {endpoints.api}/models</c>. The endpoint enumerates
/// every model the authenticated user is entitled to invoke through GitHub Copilot.
/// </summary>
public sealed class CopilotModelsResponse
{
    [JsonPropertyName("data")]
    public List<CopilotModelInfo>? Data { get; set; }

    [JsonPropertyName("object")]
    public string? Object { get; set; }
}

/// <summary>
/// Metadata for a single Copilot-hosted model. Captures the fields used by the
/// <c>copilot models</c> CLI command for display; full provider-specific tuning
/// metadata is intentionally not modelled.
/// </summary>
public sealed class CopilotModelInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("vendor")]
    public string? Vendor { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("model_picker_enabled")]
    public bool ModelPickerEnabled { get; set; }

    [JsonPropertyName("preview")]
    public bool Preview { get; set; }

    [JsonPropertyName("capabilities")]
    public CopilotModelCapabilities? Capabilities { get; set; }

    [JsonPropertyName("billing")]
    public CopilotModelBilling? Billing { get; set; }
}

public sealed class CopilotModelCapabilities
{
    [JsonPropertyName("family")]
    public string? Family { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("tokenizer")]
    public string? Tokenizer { get; set; }

    [JsonPropertyName("supports")]
    public CopilotModelSupports? Supports { get; set; }

    [JsonPropertyName("limits")]
    public Dictionary<string, double>? Limits { get; set; }
}

public sealed class CopilotModelSupports
{
    [JsonPropertyName("streaming")]
    public bool Streaming { get; set; }

    [JsonPropertyName("tool_calls")]
    public bool ToolCalls { get; set; }

    [JsonPropertyName("parallel_tool_calls")]
    public bool ParallelToolCalls { get; set; }

    [JsonPropertyName("vision")]
    public bool Vision { get; set; }

    [JsonPropertyName("structured_outputs")]
    public bool StructuredOutputs { get; set; }
}

public sealed class CopilotModelBilling
{
    [JsonPropertyName("is_premium")]
    public bool IsPremium { get; set; }

    [JsonPropertyName("multiplier")]
    public double Multiplier { get; set; }

    [JsonPropertyName("restricted_to")]
    public List<string>? RestrictedTo { get; set; }
}
