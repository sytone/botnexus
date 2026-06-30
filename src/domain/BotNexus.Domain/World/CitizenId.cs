using System.Text.Json;
using System.Text.Json.Serialization;
using BotNexus.Domain.Primitives;

namespace BotNexus.Domain.World;

/// <summary>
/// A discriminated-union identity for a <see cref="ICitizen"/> — carries either a
/// <see cref="UserId"/> or an <see cref="AgentId"/>. Used by cross-cutting code
/// (channel routing, permissions, audit, session participants) so a single typed
/// identity can refer to either species without losing the species discriminator.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CitizenId"/> is a sum type, not a Vogen value object: it wraps two
/// distinct inner identities and chooses between them via <see cref="Kind"/>. The
/// inner <see cref="UserId"/> and <see cref="AgentId"/> are themselves Vogen-typed
/// value objects.
/// </para>
/// <para>
/// Constructed only via <see cref="Of(UserId)"/> / <see cref="Of(AgentId)"/> — there is
/// no public constructor and no public init-property surface that could yield a
/// mixed-arm record. <c>default(CitizenId)</c> has <see cref="Kind"/> equal to
/// <see cref="CitizenKind.Unknown"/> and is rejected by <see cref="Match{T}"/> and
/// <see cref="Value"/>; equality of two well-formed <c>CitizenId</c> instances depends
/// only on <see cref="Kind"/> and the populated arm.
/// </para>
/// </remarks>
[JsonConverter(typeof(CitizenIdJsonConverter))]
public readonly struct CitizenId : IEquatable<CitizenId>
{
    private readonly UserId? _asUser;
    private readonly AgentId? _asAgent;

    private CitizenId(CitizenKind kind, UserId? asUser, AgentId? asAgent)
    {
        Kind = kind;
        _asUser = asUser;
        _asAgent = asAgent;
    }

    /// <summary>The species of this citizen.</summary>
    public CitizenKind Kind { get; }

    /// <summary>The user identity when <see cref="Kind"/> is <see cref="CitizenKind.User"/>; otherwise <c>null</c>.</summary>
    public UserId? AsUser => Kind == CitizenKind.User ? _asUser : null;

    /// <summary>The agent identity when <see cref="Kind"/> is <see cref="CitizenKind.Agent"/>; otherwise <c>null</c>.</summary>
    public AgentId? AsAgent => Kind == CitizenKind.Agent ? _asAgent : null;

    /// <summary>True when this is a well-formed citizen (i.e. not <c>default(CitizenId)</c>).</summary>
    public bool IsValid => Kind != CitizenKind.Unknown;

    /// <summary>
    /// The underlying identifier string for the populated arm. Use with care — discards the
    /// <see cref="Kind"/> discriminator so two citizens of different species with the same
    /// inner string compare equal at the string level. Persistence and indexing should prefer
    /// <see cref="ToString"/>, which includes the kind prefix, or serialise <see cref="Kind"/>
    /// separately.
    /// </summary>
    public string Value => Kind switch
    {
        CitizenKind.User => _asUser!.Value.Value,
        CitizenKind.Agent => _asAgent!.Value.Value,
        _ => throw new InvalidOperationException("CitizenId is uninitialized; use Of(UserId) or Of(AgentId) to construct one."),
    };

    /// <summary>Creates a <see cref="CitizenId"/> for a user citizen.</summary>
    public static CitizenId Of(UserId id) => new(CitizenKind.User, id, null);

    /// <summary>Creates a <see cref="CitizenId"/> for an agent citizen.</summary>
    public static CitizenId Of(AgentId id) => new(CitizenKind.Agent, null, id);

    /// <summary>
    /// Folds this citizen identity into a single result, dispatching on <see cref="Kind"/>.
    /// Throws <see cref="InvalidOperationException"/> for <c>default(CitizenId)</c>.
    /// </summary>
    public T Match<T>(Func<UserId, T> onUser, Func<AgentId, T> onAgent)
    {
        ArgumentNullException.ThrowIfNull(onUser);
        ArgumentNullException.ThrowIfNull(onAgent);

        return Kind switch
        {
            CitizenKind.User => onUser(_asUser!.Value),
            CitizenKind.Agent => onAgent(_asAgent!.Value),
            _ => throw new InvalidOperationException("CitizenId is uninitialized; use Of(UserId) or Of(AgentId) to construct one."),
        };
    }

    /// <summary>Diagnostic / persistence-key form: <c>user:&lt;id&gt;</c> or <c>agent:&lt;id&gt;</c>.</summary>
    public override string ToString() => Kind switch
    {
        CitizenKind.User => $"user:{_asUser!.Value.Value}",
        CitizenKind.Agent => $"agent:{_asAgent!.Value.Value}",
        _ => "citizen:<uninitialized>",
    };

    /// <summary>
    /// Attempts to parse a citizen identity from the canonical persistence-key form produced
    /// by <see cref="ToString"/> — <c>user:&lt;id&gt;</c> or <c>agent:&lt;id&gt;</c>. The kind
    /// prefix is matched case-insensitively; the inner identifier is preserved verbatim and
    /// must be valid for the corresponding inner value object. Returns <c>false</c> for
    /// <c>null</c>, empty/whitespace, missing-separator, empty-kind, empty-id, unknown kind,
    /// and the <c>citizen:&lt;uninitialized&gt;</c> sentinel emitted by <c>default(CitizenId)</c>.
    /// </summary>
    public static bool TryParse(string? value, out CitizenId citizen)
    {
        citizen = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            return false;

        var kindPart = value[..separator];
        var idPart = value[(separator + 1)..];

        if (string.IsNullOrWhiteSpace(kindPart) || string.IsNullOrWhiteSpace(idPart))
            return false;

        // Map the textual kind prefix onto the enum, then delegate to the (kind, id) overload
        // so the prefix-string form and the structured form share one composition path.
        return Enum.TryParse<CitizenKind>(kindPart, ignoreCase: true, out var kind)
            && TryParse(kind, idPart, out citizen);
    }

    /// <summary>
    /// Composes a citizen identity from a known <see cref="CitizenKind"/> and its inner identifier
    /// string. This is the single composition site every (kind, id) caller funnels through - the
    /// string-prefix <see cref="TryParse(string?, out CitizenId)"/> overload, the JSON converter,
    /// and persistence loaders all delegate here so the kind-to-arm mapping lives in exactly one
    /// place. Returns <see langword="false"/> for an unknown/<see cref="CitizenKind.Unknown"/> kind,
    /// a null/whitespace id, or an id the inner value object rejects - unknown kinds are surfaced
    /// as a typed failure here and logged explicitly by callers, never silently dropped.
    /// </summary>
    public static bool TryParse(CitizenKind kind, string? id, out CitizenId citizen)
    {
        citizen = default;
        if (string.IsNullOrWhiteSpace(id))
            return false;

        try
        {
            switch (kind)
            {
                case CitizenKind.User:
                    citizen = Of(UserId.From(id));
                    return true;
                case CitizenKind.Agent:
                    citizen = Of(AgentId.From(id));
                    return true;
                default:
                    return false;
            }
        }
        catch (Vogen.ValueObjectValidationException)
        {
            // Inner value-object validation rejected the id portion.
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Equals(CitizenId other) => Kind switch
    {
        CitizenKind.User => other.Kind == CitizenKind.User && _asUser!.Value == other._asUser!.Value,
        CitizenKind.Agent => other.Kind == CitizenKind.Agent && _asAgent!.Value == other._asAgent!.Value,
        _ => other.Kind == CitizenKind.Unknown,
    };

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CitizenId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Kind switch
    {
        CitizenKind.User => HashCode.Combine(CitizenKind.User, _asUser!.Value),
        CitizenKind.Agent => HashCode.Combine(CitizenKind.Agent, _asAgent!.Value),
        _ => 0,
    };

    /// <summary>Equality operator.</summary>
    public static bool operator ==(CitizenId left, CitizenId right) => left.Equals(right);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(CitizenId left, CitizenId right) => !left.Equals(right);
}

/// <summary>
/// System.Text.Json converter for <see cref="CitizenId"/>. Wire format:
/// <c>{ "kind": "User", "id": "alice" }</c> or <c>{ "kind": "Agent", "id": "coding-agent" }</c>.
/// Property names are read case-insensitively; <see cref="CitizenKind"/> tolerates both string
/// and numeric (legacy) forms.
/// </summary>
internal sealed class CitizenIdJsonConverter : JsonConverter<CitizenId>
{
    public override CitizenId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("Expected start of object for CitizenId.");

        CitizenKind? kind = null;
        string? id = null;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("Expected property name in CitizenId.");

            var propertyName = reader.GetString();
            reader.Read();

            if (string.Equals(propertyName, "kind", StringComparison.OrdinalIgnoreCase))
            {
                kind = ReadKind(ref reader);
            }
            else if (string.Equals(propertyName, "id", StringComparison.OrdinalIgnoreCase))
            {
                if (reader.TokenType == JsonTokenType.Null)
                    throw new JsonException("CitizenId.id cannot be null.");
                id = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        if (kind is null)
            throw new JsonException("CitizenId requires a 'kind' property.");
        if (id is null)
            throw new JsonException("CitizenId requires an 'id' property.");

        // Same kind-to-arm mapping as the rest of the system: delegate to the canonical
        // composition site rather than re-switching here. Unknown species fail explicitly.
        if (!CitizenId.TryParse(kind.Value, id, out var citizen))
            throw new JsonException($"CitizenId.kind '{kind}' is not a valid citizen species.");
        return citizen;
    }

    private static CitizenKind ReadKind(ref Utf8JsonReader reader) => reader.TokenType switch
    {
        JsonTokenType.String => ParseKind(reader.GetString()),
        JsonTokenType.Number => (CitizenKind)reader.GetInt32(),
        _ => throw new JsonException("CitizenId.kind must be a string or number."),
    };

    private static CitizenKind ParseKind(string? value) =>
        Enum.TryParse<CitizenKind>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new JsonException($"CitizenId.kind '{value}' is not a known CitizenKind.");

    public override void Write(Utf8JsonWriter writer, CitizenId value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind.ToString());
        writer.WriteString("id", value.Value);
        writer.WriteEndObject();
    }
}
