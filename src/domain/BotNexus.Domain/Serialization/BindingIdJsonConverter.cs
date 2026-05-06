using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Serialization;

/// <summary>
/// JSON converter that serialises/deserialises <see cref="BindingId"/> as a plain string,
/// matching the on-wire format used by <see cref="SessionIdJsonConverter"/>.
/// </summary>
public sealed class BindingIdJsonConverter : JsonConverter<BindingId>
{
    /// <inheritdoc />
    public override BindingId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("BindingId must be a string.");

        return BindingId.From(reader.GetString() ?? string.Empty);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, BindingId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
