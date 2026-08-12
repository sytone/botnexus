using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.IO.Abstractions;
using NJsonSchema;
using NJsonSchema.Generation;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Generates and validates JSON schema for <see cref="PlatformConfig"/>.
/// </summary>
public static class PlatformConfigSchema
{
    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Lazy<JsonSchema> CachedSchema = new(GenerateSchemaInternal);

    public static string GenerateSchemaJson()
        => CachedSchema.Value.ToJson();

    public static void WriteSchema(string outputPath, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fs = fileSystem ?? new FileSystem();

        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            fs.Directory.CreateDirectory(directory);

        fs.File.WriteAllText(fullPath, GenerateSchemaJson());
    }

    public static IReadOnlyList<string> ValidateJson(string json, IFileSystem? fileSystem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        try
        {
            var normalizedJson = NormalizePropertyCasing(json);
            return CachedSchema.Value
                .Validate(normalizedJson)
                .Select(error => string.IsNullOrWhiteSpace(error.Path)
                    ? $"schema: {error.Kind} ({error})"
                    : $"schema.{error.Path.TrimStart('#', '/', '$', '.').Replace("/", ".")}: {error.Kind} ({error})")
                .ToArray();
        }
        catch (Exception ex)
        {
            return [$"schema: invalid JSON. {ex.Message}"];
        }
    }

    public static IReadOnlyList<string> ValidateObject(PlatformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var json = JsonSerializer.Serialize(config, WriteJsonOptions);
        return ValidateJson(json);
    }

    private static string NormalizePropertyCasing(string json)
    {
        var node = JsonNode.Parse(json);
        return NormalizeNode(node)?.ToJsonString() ?? "{}";
    }

    /// <summary>
    /// Recursively camelCases property names so a hand-edited config that uses PascalCase still
    /// validates against the camelCase schema, while leaving values untouched.
    /// </summary>
    /// <remarks>
    /// <para>#3036: normalisation must NOT touch the <c>FeatureManagement</c> section. Every other
    /// property is serialised through <c>JsonNamingPolicy.CamelCase</c>, but
    /// <see cref="PlatformConfig.FeatureManagement"/> carries an explicit
    /// <see cref="JsonPropertyNameAttribute"/> pinning it to PascalCase because
    /// Microsoft.FeatureManagement binds the PascalCase section name. NJsonSchema generates the
    /// schema from that same attribute, so the schema expects <c>FeatureManagement</c> - and
    /// camelCasing it here produced a key the closed root schema had never heard of, failing with
    /// <c>NoAdditionalPropertiesAllowed: #/featureManagement</c> and aborting startup at the first
    /// <c>IOptionsMonitor&lt;PlatformConfig&gt;.CurrentValue</c>. The normaliser was un-naming the one
    /// property that was explicitly named to resist being un-named, which made EVERY feature flag
    /// unsettable by every route (doctor --fix, hand edit, env var, <c>botnexus config set</c>).</para>
    /// <para>The section's <em>contents</em> are exempt for a second, independent reason: flag names
    /// are free-form keys (the subschema is <c>additionalProperties: {}</c>) matched verbatim by
    /// <see cref="FeatureFlags"/>, so rewriting <c>ConfigStoreShadowMigration</c> to
    /// <c>configStoreShadowMigration</c> would silently unbind the flag while leaving the file
    /// looking correct - the same silent-drift shape as #2764 and #2816.</para>
    /// </remarks>
    private static JsonNode? NormalizeNode(JsonNode? node, bool preserveCasing = false)
    {
        if (node is null)
            return null;

        if (node is JsonObject jsonObject)
        {
            var normalized = new JsonObject();
            foreach (var property in jsonObject)
            {
                // '$'-prefixed keys ($schema) keep their casing. The FeatureManagement section is
                // canonicalised to its PascalCase section name (so a hand-edited lowercase
                // "featureManagement" is repaired rather than rejected), and once inside that
                // section every descendant key is preserved verbatim.
                var isFeatureSection = !preserveCasing
                    && string.Equals(property.Key, FeatureFlags.SectionName, StringComparison.OrdinalIgnoreCase);
                var key = isFeatureSection
                    ? FeatureFlags.SectionName
                    : preserveCasing || property.Key.StartsWith('$')
                        ? property.Key
                        : ToCamelCase(property.Key);
                normalized[key] = NormalizeNode(property.Value, preserveCasing || isFeatureSection);
            }

            return normalized;
        }

        if (node is JsonArray jsonArray)
        {
            var normalizedArray = new JsonArray();
            foreach (var item in jsonArray)
                normalizedArray.Add(NormalizeNode(item, preserveCasing));
            return normalizedArray;
        }

        return node.DeepClone();
    }

    private static string ToCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (value.Length == 1)
            return value.ToLowerInvariant();

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static JsonSchema GenerateSchemaInternal()
    {
        var settings = new SystemTextJsonSchemaGeneratorSettings
        {
            SerializerOptions = WriteJsonOptions
        };

        var schema = JsonSchema.FromType<PlatformConfig>(settings);
        schema.Title = "BotNexus Platform Configuration";
        return schema;
    }
}
