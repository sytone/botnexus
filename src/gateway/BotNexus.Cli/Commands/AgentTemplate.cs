using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Cli.Commands;

/// <summary>
/// Versioned, redacted agent template (<c>agentTemplate/v1</c>) produced by
/// <c>botnexus agent export</c>. Contains only safe-to-share descriptor fields and a
/// <see cref="RequiredSecrets"/> manifest enumerating the provider credential keys the
/// agent needs - never the secret values themselves.
/// </summary>
public sealed class AgentTemplate
{
    /// <summary>Schema discriminator. Always <see cref="CurrentSchema"/> for v1 documents.</summary>
    public const string CurrentSchema = "agentTemplate/v1";

    [JsonPropertyName("schema")]
    public string Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("agent")]
    public AgentTemplateDescriptor Agent { get; set; } = new();

    [JsonPropertyName("requiredSecrets")]
    public List<RequiredSecret> RequiredSecrets { get; set; } = [];

    /// <summary>
    /// JSON options used for round-trippable export/import: camelCase, indented,
    /// null-skipping, and thinking-level enum written in wire form.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(
            new JsonStringEnumConverter<BotNexus.Agent.Providers.Core.Models.ThinkingLevel>());
        return options;
    }

    /// <summary>Serialize this template to the canonical redacted JSON form.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>Parse a template document from JSON. Returns null on malformed input.</summary>
    public static AgentTemplate? FromJson(string json)
        => JsonSerializer.Deserialize<AgentTemplate>(json, SerializerOptions);

    /// <summary>
    /// Validate the structural invariants of an <c>agentTemplate/v1</c> document.
    /// Returns the list of validation errors (empty when valid).
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
            errors.Add($"Unsupported schema '{Schema}'. Expected '{CurrentSchema}'.");
        if (Agent is null)
            errors.Add("Template is missing the 'agent' descriptor.");
        else
        {
            if (string.IsNullOrWhiteSpace(Agent.ModelId))
                errors.Add("Template agent.modelId is required.");
            if (string.IsNullOrWhiteSpace(Agent.ApiProvider))
                errors.Add("Template agent.apiProvider is required.");
        }

        return errors;
    }
}

/// <summary>Safe-to-share descriptor fields of an exported agent template.</summary>
public sealed class AgentTemplateDescriptor
{
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("emoji")]
    public string? Emoji { get; set; }

    [JsonPropertyName("modelId")]
    public string? ModelId { get; set; }

    [JsonPropertyName("apiProvider")]
    public string? ApiProvider { get; set; }

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("toolIds")]
    public List<string>? ToolIds { get; set; }

    [JsonPropertyName("thinking")]
    public string? Thinking { get; set; }

    [JsonPropertyName("contextWindow")]
    public int? ContextWindow { get; set; }
}

/// <summary>
/// A single entry in the <see cref="AgentTemplate.RequiredSecrets"/> manifest. Names a
/// provider credential the importing environment must supply - carries no secret value.
/// </summary>
public sealed class RequiredSecret
{
    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}
