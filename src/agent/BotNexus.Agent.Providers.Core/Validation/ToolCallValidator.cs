using System.Text.Json;

namespace BotNexus.Agent.Providers.Core.Validation;

/// <summary>
/// Validates tool call arguments against the tool's JSON Schema parameters.
/// Called after LLM response parsing, before tool dispatch.
/// </summary>
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
    {
        if (parameterSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return (true, []);
        }

        var errors = new List<string>();

        ValidateRequired(arguments, parameterSchema, errors);
        ValidateTopLevelProperties(arguments, parameterSchema, errors);

        return errors.Count == 0
            ? (true, [])
            : (false, errors.ToArray());
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
            $"Property '{argumentProperty.Name}' must be of type {string.Join(" or ", allowedTypes)}.");
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
