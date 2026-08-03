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

    private static JsonNode? NormalizeNode(JsonNode? node)
    {
        if (node is null)
            return null;

        if (node is JsonObject jsonObject)
        {
            var normalized = new JsonObject();
            foreach (var property in jsonObject)
            {
                var key = property.Key.StartsWith('$')
                    ? property.Key
                    : ToCamelCase(property.Key);
                normalized[key] = NormalizeNode(property.Value);
            }

            return normalized;
        }

        if (node is JsonArray jsonArray)
        {
            var normalizedArray = new JsonArray();
            foreach (var item in jsonArray)
                normalizedArray.Add(NormalizeNode(item));
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
