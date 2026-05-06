using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Serialization;

/// <summary>
/// JSON converter that serialises/deserialises <see cref="ThreadId"/> as a plain string.
/// </summary>
public sealed class ThreadIdJsonConverter : JsonConverter<ThreadId>
{
    /// <inheritdoc />
    public override ThreadId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("ThreadId must be a string.");

        return ThreadId.From(reader.GetString() ?? string.Empty);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ThreadId value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
