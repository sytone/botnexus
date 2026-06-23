using System.Text.Json;

namespace BotNexus.Agent.Providers.Core.Validation;

/// <summary>
/// Validates tool call arguments against the tool's JSON Schema parameters.
/// Called after LLM response parsing, before tool dispatch.
/// </summary>
/// <remarks>
/// Validation is preceded by a losslessly-safe coercion pass (issue #1552): a model
/// frequently emits a string-encoded integer (<c>"300"</c>) or a bare scalar where an
/// array is expected (<c>"platform"</c> instead of <c>["platform"]</c>). The tools
/// themselves already tolerate these shapes downstream (e.g. <c>AskUserTool.ReadInt</c>
/// parses numeric strings, <c>MemorySaveTool.ParseTags</c> only honours arrays), so a
/// strict reject-only validator would burn a turn for no benefit. The coercion pass
/// rewrites the corrected shape and returns it so the downstream tool receives the
/// fixed arguments; genuinely-wrong shapes (e.g. an object where an array is expected)
/// are still rejected, now with the received kind in the message.
/// </remarks>
public static class ToolCallValidator
{
    /// <summary>
    /// Validates arguments against the tool's parameter schema.
    /// Returns (isValid, errors) — invalid calls get an error ToolResult
    /// instead of dispatching to the tool.
    /// </summary>
    /// <param name="arguments">Tool call arguments to validate.</param>
    /// <param name="parameterSchema">JSON Schema parameter definition from the tool.</param>
    /// <returns>A tuple containing validation status and any validation errors.</returns>
    public static (bool IsValid, string[] Errors) Validate(
        JsonElement arguments,
        JsonElement parameterSchema)
        => Validate(arguments, parameterSchema, out _);

    /// <summary>
    /// Validates arguments against the tool's parameter schema, first coercing
    /// losslessly-safe shape mismatches (string-encoded numbers/booleans and
    /// scalar-for-array). The coerced arguments are returned via
    /// <paramref name="coercedArguments"/> so the caller can dispatch the corrected
    /// shape to the tool.
    /// </summary>
    /// <param name="arguments">Tool call arguments to validate.</param>
    /// <param name="parameterSchema">JSON Schema parameter definition from the tool.</param>
    /// <param name="coercedArguments">
    /// The arguments after coercion. Equal to <paramref name="arguments"/> when no
    /// coercion was applicable (or when arguments are not a JSON object).
    /// </param>
    /// <returns>A tuple containing validation status and any validation errors.</returns>
    public static (bool IsValid, string[] Errors) Validate(
        JsonElement arguments,
        JsonElement parameterSchema,
        out JsonElement coercedArguments)
    {
        coercedArguments = arguments;

        if (parameterSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return (true, []);
        }

        coercedArguments = CoerceArguments(arguments, parameterSchema);

        var errors = new List<string>();

        ValidateRequired(coercedArguments, parameterSchema, errors);
        ValidateTopLevelProperties(coercedArguments, parameterSchema, errors);
        ValidateAdditionalProperties(coercedArguments, parameterSchema, errors);

        return errors.Count == 0
            ? (true, [])
            : (false, errors.ToArray());
    }

    /// <summary>
    /// Produces a coerced copy of <paramref name="arguments"/> where each property whose
    /// supplied kind mismatches its schema type is rewritten to the schema type when the
    /// conversion is lossless and safe. Returns the original element when arguments are
    /// not an object or nothing needed coercing.
    /// </summary>
    private static JsonElement CoerceArguments(JsonElement arguments, JsonElement schema)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        if (!schema.TryGetProperty("properties", out var propertiesElement) ||
            propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return arguments;
        }

        var changed = false;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var argument in arguments.EnumerateObject())
            {
                writer.WritePropertyName(argument.Name);

                if (propertiesElement.TryGetProperty(argument.Name, out var propertySchema) &&
                    propertySchema.ValueKind == JsonValueKind.Object &&
                    TryCoerceValue(argument.Value, propertySchema, writer))
                {
                    changed = true;
                }
                else
                {
                    argument.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        if (!changed)
        {
            return arguments;
        }

        using var coercedDocument = JsonDocument.Parse(buffer.ToArray());
        return coercedDocument.RootElement.Clone();
    }

    /// <summary>
    /// Attempts to write a coerced form of <paramref name="value"/> matching the schema
    /// type to <paramref name="writer"/>. Returns <c>true</c> when a coercion was applied
    /// (and written); <c>false</c> when no safe coercion applies (caller writes the
    /// original value verbatim).
    /// </summary>
    private static bool TryCoerceValue(JsonElement value, JsonElement propertySchema, Utf8JsonWriter writer)
    {
        if (!propertySchema.TryGetProperty("type", out var typeElement))
        {
            return false;
        }

        var allowedTypes = GetAllowedTypes(typeElement);
        if (allowedTypes.Count == 0)
        {
            return false;
        }

        // Already matches an allowed type — no coercion needed.
        if (allowedTypes.Any(type => MatchesType(value, type)))
        {
            return false;
        }

        // string -> integer / number / boolean (lossless round-trip only).
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();

            if (allowedTypes.Contains("integer") && TryParseInteger(text, out var integerValue))
            {
                writer.WriteNumberValue(integerValue);
                return true;
            }

            if (allowedTypes.Contains("number") && decimal.TryParse(text, out var numberValue))
            {
                writer.WriteNumberValue(numberValue);
                return true;
            }

            if (allowedTypes.Contains("boolean") && TryParseBoolean(text, out var boolValue))
            {
                writer.WriteBooleanValue(boolValue);
                return true;
            }
        }

        // scalar -> single-element array (optionally comma-split for string items).
        if (allowedTypes.Contains("array") && IsScalar(value))
        {
            WriteScalarAsArray(value, propertySchema, writer);
            return true;
        }

        return false;
    }

    private static void WriteScalarAsArray(JsonElement value, JsonElement propertySchema, Utf8JsonWriter writer)
    {
        writer.WriteStartArray();

        if (value.ValueKind == JsonValueKind.String &&
            SchemaItemsAreStrings(propertySchema) &&
            value.GetString() is { } text &&
            text.Contains(','))
        {
            foreach (var part in text.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    writer.WriteStringValue(trimmed);
                }
            }
        }
        else
        {
            value.WriteTo(writer);
        }

        writer.WriteEndArray();
    }

    private static bool SchemaItemsAreStrings(JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!items.TryGetProperty("type", out var itemType))
        {
            return false;
        }

        return GetAllowedTypes(itemType).Contains("string");
    }

    private static bool IsScalar(JsonElement value)
        => value.ValueKind is JsonValueKind.String
            or JsonValueKind.Number
            or JsonValueKind.True
            or JsonValueKind.False;

    private static bool TryParseInteger(string? text, out long value)
    {
        value = 0;
        return !string.IsNullOrWhiteSpace(text) && long.TryParse(text, out value);
    }

    private static bool TryParseBoolean(string? text, out bool value)
    {
        value = false;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return bool.TryParse(text, out value);
    }

    private static void ValidateRequired(JsonElement arguments, JsonElement schema, ICollection<string> errors)
    {
        if (!schema.TryGetProperty("required", out var requiredElement) || requiredElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            errors.Add("Arguments must be a JSON object.");
            return;
        }

        foreach (var required in requiredElement.EnumerateArray())
        {
            if (required.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = required.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!arguments.TryGetProperty(name, out _))
            {
                errors.Add($"Missing required property '{name}'.");
            }
        }
    }

    private static void ValidateTopLevelProperties(JsonElement arguments, JsonElement schema, ICollection<string> errors)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!schema.TryGetProperty("properties", out var propertiesElement) || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var argumentProperty in arguments.EnumerateObject())
        {
            if (!propertiesElement.TryGetProperty(argumentProperty.Name, out var propertySchema) ||
                propertySchema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            ValidateType(argumentProperty, propertySchema, errors);
            ValidateEnum(argumentProperty, propertySchema, errors);
        }
    }

    private static void ValidateAdditionalProperties(JsonElement arguments, JsonElement schema, ICollection<string> errors)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!schema.TryGetProperty("additionalProperties", out var additionalProps))
        {
            return;
        }

        if (additionalProps.ValueKind != JsonValueKind.False)
        {
            return;
        }

        if (!schema.TryGetProperty("properties", out var propertiesElement) || propertiesElement.ValueKind != JsonValueKind.Object)
        {
            // No properties defined but additionalProperties is false — all properties are unknown
            foreach (var argumentProperty in arguments.EnumerateObject())
            {
                errors.Add($"Property '{argumentProperty.Name}' is not defined in the schema.");
            }
            return;
        }

        foreach (var argumentProperty in arguments.EnumerateObject())
        {
            if (!propertiesElement.TryGetProperty(argumentProperty.Name, out _))
            {
                errors.Add($"Property '{argumentProperty.Name}' is not defined in the schema.");
            }
        }
    }

    private static void ValidateType(JsonProperty argumentProperty, JsonElement propertySchema, ICollection<string> errors)
    {
        if (!propertySchema.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var allowedTypes = GetAllowedTypes(typeElement);
        if (allowedTypes.Count == 0)
        {
            return;
        }

        if (allowedTypes.Any(type => MatchesType(argumentProperty.Value, type)))
        {
            return;
        }

        errors.Add(
            $"Property '{argumentProperty.Name}' must be of type {string.Join(" or ", allowedTypes)} " +
            $"(received {DescribeValue(argumentProperty.Value)}).");
    }

    /// <summary>
    /// Renders a short, safe description of the received value for diagnostics — the JSON
    /// kind plus the literal for short scalars (quoted for strings). Long or structured
    /// values are summarised by kind only to keep the error message bounded.
    /// </summary>
    private static string DescribeValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (text.Length > 40)
                {
                    text = text[..40] + "…";
                }

                return $"string \"{text}\"";
            case JsonValueKind.Number:
                return $"number {value.GetRawText()}";
            case JsonValueKind.True:
            case JsonValueKind.False:
                return $"boolean {value.GetRawText()}";
            case JsonValueKind.Array:
                return "array";
            case JsonValueKind.Object:
                return "object";
            case JsonValueKind.Null:
                return "null";
            default:
                return value.ValueKind.ToString().ToLowerInvariant();
        }
    }

    private static void ValidateEnum(JsonProperty argumentProperty, JsonElement propertySchema, ICollection<string> errors)
    {
        if (!propertySchema.TryGetProperty("enum", out var enumElement) || enumElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var allowedValue in enumElement.EnumerateArray())
        {
            if (JsonElementsEqual(argumentProperty.Value, allowedValue))
            {
                return;
            }
        }

        errors.Add($"Property '{argumentProperty.Name}' must be one of the allowed enum values.");
    }

    private static List<string> GetAllowedTypes(JsonElement typeElement)
    {
        var allowedTypes = new List<string>();

        switch (typeElement.ValueKind)
        {
            case JsonValueKind.String:
                var singleType = typeElement.GetString();
                if (!string.IsNullOrWhiteSpace(singleType))
                {
                    allowedTypes.Add(singleType);
                }

                break;

            case JsonValueKind.Array:
                foreach (var type in typeElement.EnumerateArray())
                {
                    if (type.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var typeName = type.GetString();
                    if (!string.IsNullOrWhiteSpace(typeName))
                    {
                        allowedTypes.Add(typeName);
                    }
                }

                break;
        }

        return allowedTypes;
    }

    private static bool MatchesType(JsonElement value, string schemaType)
    {
        return schemaType switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };
    }

    private static bool IsInteger(JsonElement value)
    {
        if (value.TryGetInt64(out _) || value.TryGetUInt64(out _))
        {
            return true;
        }

        if (!value.TryGetDecimal(out var decimalValue))
        {
            return false;
        }

        return decimal.Truncate(decimalValue) == decimalValue;
    }

    private static bool JsonElementsEqual(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            if (left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number)
            {
                return NumbersEqual(left, right);
            }

            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => NumbersEqual(left, right),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null => true,
            _ => string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal)
        };
    }

    private static bool NumbersEqual(JsonElement left, JsonElement right)
    {
        if (left.TryGetDecimal(out var leftDecimal) && right.TryGetDecimal(out var rightDecimal))
        {
            return leftDecimal == rightDecimal;
        }

        return string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);
    }
}
