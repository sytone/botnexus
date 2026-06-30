using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Configuration;

/// <summary>
/// Handles backward compatibility for the titling config field.
/// Previously <c>gateway.auxiliary.titling</c> was a plain string (model name).
/// Now it is a <see cref="TitlingConfig"/> object. This converter accepts both forms.
/// </summary>
internal sealed class TitlingConfigJsonConverter : JsonConverter<TitlingConfig?>
{
    public override TitlingConfig? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.String)
        {
            var model = reader.GetString();
            return string.IsNullOrWhiteSpace(model) ? null : new TitlingConfig { Model = model };
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;

            var config = new TitlingConfig();
            if (root.TryGetProperty("enabled", out var enabledEl) || root.TryGetProperty("Enabled", out enabledEl))
                config.Enabled = enabledEl.GetBoolean();
            if (root.TryGetProperty("model", out var modelEl) || root.TryGetProperty("Model", out modelEl))
                config.Model = modelEl.GetString();
            if (root.TryGetProperty("timeoutSeconds", out var timeoutEl) || root.TryGetProperty("TimeoutSeconds", out timeoutEl))
                config.TimeoutSeconds = timeoutEl.GetInt32();

            return config;
        }

        throw new JsonException($"Unexpected token type '{reader.TokenType}' when reading TitlingConfig.");
    }

    public override void Write(Utf8JsonWriter writer, TitlingConfig? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteBoolean("enabled", value.Enabled);
        if (value.Model is not null)
        {
            writer.WriteString("model", value.Model);
        }
        writer.WriteNumber("timeoutSeconds", value.TimeoutSeconds);
        writer.WriteEndObject();
    }
}