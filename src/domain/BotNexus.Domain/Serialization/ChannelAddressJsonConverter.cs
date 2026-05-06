using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.Serialization;

/// <summary>
/// JSON converter that serialises/deserialises <see cref="ChannelAddress"/> as a plain string.
/// An empty string round-trips to <see cref="ChannelAddress.Empty"/>.
/// </summary>
public sealed class ChannelAddressJsonConverter : JsonConverter<ChannelAddress>
{
    /// <inheritdoc />
    public override ChannelAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("ChannelAddress must be a string.");

        return ChannelAddress.From(reader.GetString() ?? string.Empty);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ChannelAddress value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
