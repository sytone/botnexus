using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Domain.World;

namespace BotNexus.Domain.Primitives;

/// <summary>
/// A participant in a <see cref="BotNexus.Gateway.Abstractions.Models.GatewaySession"/> —
/// either a user (human) or an agent identified via the discriminated <see cref="CitizenId"/>
/// union. The optional <see cref="Role"/> distinguishes initiator from peer, caller from
/// callee, etc.
/// </summary>
/// <remarks>
/// <para>
/// Persistence note: existing session stores (<c>SqliteSessionStore.participants_json</c> and
/// <c>FileSessionStore</c> metadata sidecars) may contain blobs written before Phase 1.5 in
/// the legacy shape <c>{ "type": 0|1|"User"|"Agent", "id": "...", "worldId": "...", "role": "..." }</c>.
/// The custom <see cref="SessionParticipantJsonConverter"/> reads both shapes and, during the
/// transition window, writes both shapes so a rollback can still read participants written by
/// post-migration code. The legacy <see cref="ParticipantType"/> enum remains for read
/// back-compat only.
/// </para>
/// </remarks>
[JsonConverter(typeof(SessionParticipantJsonConverter))]
public sealed record SessionParticipant
{
    /// <summary>The discriminated citizen identity of this participant.</summary>
    public required CitizenId CitizenId { get; init; }

    /// <summary>Optional role label (e.g., <c>"initiator"</c>, <c>"caller"</c>, <c>"peer"</c>).</summary>
    public string? Role { get; init; }
}

/// <summary>
/// Legacy discriminator for <see cref="SessionParticipant"/> persisted before Phase 1.5
/// introduced <see cref="CitizenId"/>. Retained for one release so existing session blobs
/// continue to deserialise; new code should consume <see cref="CitizenKind"/> via
/// <see cref="SessionParticipant.CitizenId"/>.
/// </summary>
public enum ParticipantType
{
    /// <summary>The participant is a human user. Maps to <see cref="CitizenKind.User"/>.</summary>
    User,
    /// <summary>The participant is a named agent. Maps to <see cref="CitizenKind.Agent"/>.</summary>
    Agent,
}

/// <summary>
/// System.Text.Json converter for <see cref="SessionParticipant"/>. Handles two wire formats:
/// <list type="bullet">
///   <item>New shape: <c>{ "citizenId": { "kind": "User"|"Agent", "id": "..." }, "role": "..." }</c>.</item>
///   <item>Legacy shape: <c>{ "type": 0|1|"User"|"Agent", "id": "...", "worldId": "...", "role": "..." }</c>.</item>
/// </list>
/// Reads either shape (property names case-insensitive). For one release the converter writes
/// <em>both</em> shapes so a rollback to pre-Phase-1.5 code can still read newly-persisted
/// participants. After the rollback window, the legacy write path will be removed; readers
/// will keep handling the legacy shape until the back-compat window closes.
/// </summary>
internal sealed class SessionParticipantJsonConverter : JsonConverter<SessionParticipant>
{
    public override SessionParticipant Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected start of object for SessionParticipant.");

        CitizenId? citizenIdFromNewShape = null;
        CitizenKind? legacyKind = null;
        string? legacyId = null;
        string? role = null;
        var sawLegacyType = false;
        var sawLegacyId = false;
        var sawNewCitizenId = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name in SessionParticipant.");

            var propertyName = reader.GetString();
            reader.Read();

            if (string.Equals(propertyName, "citizenId", StringComparison.OrdinalIgnoreCase))
            {
                sawNewCitizenId = true;
                citizenIdFromNewShape = JsonSerializer.Deserialize<CitizenId>(ref reader, options);
            }
            else if (string.Equals(propertyName, "type", StringComparison.OrdinalIgnoreCase))
            {
                sawLegacyType = true;
                legacyKind = ReadLegacyType(ref reader);
            }
            else if (string.Equals(propertyName, "id", StringComparison.OrdinalIgnoreCase))
            {
                sawLegacyId = true;
                if (reader.TokenType == JsonTokenType.Null)
                    throw new JsonException("SessionParticipant.id cannot be null.");
                legacyId = reader.GetString();
            }
            else if (string.Equals(propertyName, "role", StringComparison.OrdinalIgnoreCase))
            {
                role = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
            }
            else
            {
                // Tolerate and skip unknown fields (e.g. legacy "worldId").
                reader.Skip();
            }
        }

        if (sawNewCitizenId && (sawLegacyType || sawLegacyId))
        {
            var fromLegacy = TryBuildLegacy(legacyKind, legacyId);
            if (fromLegacy is null || !fromLegacy.Equals(citizenIdFromNewShape!.Value))
                throw new JsonException("SessionParticipant has both new and legacy identity shapes and they disagree.");
        }

        var resolvedCitizenId = sawNewCitizenId
            ? citizenIdFromNewShape!.Value
            : TryBuildLegacy(legacyKind, legacyId)
                ?? throw new JsonException("SessionParticipant requires either 'citizenId' or legacy 'type'+'id'.");

        return new SessionParticipant { CitizenId = resolvedCitizenId, Role = role };
    }

    private static CitizenKind ReadLegacyType(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var raw = reader.GetString();
                if (string.IsNullOrWhiteSpace(raw))
                    throw new JsonException("SessionParticipant.type string cannot be empty.");
                if (Enum.TryParse<ParticipantType>(raw, ignoreCase: true, out var parsed))
                    return ToCitizenKind(parsed);
                throw new JsonException($"SessionParticipant.type '{raw}' is not a known ParticipantType.");
            case JsonTokenType.Number:
                if (!reader.TryGetInt32(out var asInt))
                    throw new JsonException("SessionParticipant.type number must be a 32-bit integer.");
                if (!Enum.IsDefined(typeof(ParticipantType), asInt))
                    throw new JsonException($"SessionParticipant.type numeric value {asInt} is not a known ParticipantType.");
                return ToCitizenKind((ParticipantType)asInt);
            default:
                throw new JsonException("SessionParticipant.type must be a string or number.");
        }
    }

    private static CitizenKind ToCitizenKind(ParticipantType type) => type switch
    {
        ParticipantType.User => CitizenKind.User,
        ParticipantType.Agent => CitizenKind.Agent,
        _ => throw new JsonException($"ParticipantType '{type}' has no CitizenKind mapping."),
    };

    private static CitizenId? TryBuildLegacy(CitizenKind? kind, string? id)
    {
        if (kind is null || id is null)
            return null;

        return kind.Value switch
        {
            CitizenKind.User => CitizenId.Of(UserId.From(id)),
            CitizenKind.Agent => CitizenId.Of(AgentId.From(id)),
            _ => null,
        };
    }

    public override void Write(Utf8JsonWriter writer, SessionParticipant value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("citizenId");
        JsonSerializer.Serialize(writer, value.CitizenId, options);

        // Legacy fields are emitted for one-release rollback safety; readers prefer "citizenId".
        writer.WriteString("type", ToParticipantType(value.CitizenId.Kind).ToString());
        writer.WriteString("id", value.CitizenId.Value);

        if (value.Role is not null)
            writer.WriteString("role", value.Role);

        writer.WriteEndObject();
    }

    private static ParticipantType ToParticipantType(CitizenKind kind) => kind switch
    {
        CitizenKind.User => ParticipantType.User,
        CitizenKind.Agent => ParticipantType.Agent,
        _ => throw new JsonException($"CitizenKind '{kind}' cannot be persisted as a ParticipantType."),
    };
}
