using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Domain.World;

/// <summary>
/// Discriminates the species sub-kind of an <see cref="BotNexus.Gateway.Abstractions.Models.AgentDescriptor"/>.
/// A named agent is a first-class World citizen registered by configuration or REST; a
/// sub-agent is a runtime-spawned ephemeral helper created by another agent via
/// <c>spawn_subagent</c>.
/// </summary>
/// <remarks>
/// <para>
/// This enum is the canonical typed signal for "is this descriptor a sub-agent?" — it
/// replaces the prior pattern of inferring sub-agent status by substring-matching
/// <c>::subagent::</c> inside <c>SessionId</c>. The <see cref="SubAgent"/> value is set
/// exactly once, by <c>DefaultSubAgentManager.SpawnAsync</c>, on the child registration
/// it creates; configuration-loaded descriptors must remain <see cref="Named"/>.
/// </para>
/// <para>
/// <see cref="Named"/> is the default so existing JSON / config / REST payloads that
/// omit <c>kind</c> round-trip with the correct intent. The
/// <c>AgentDescriptorValidator.ValidateForConfig</c> overload rejects config-loaded
/// descriptors that attempt to assert <see cref="SubAgent"/> directly — sub-agents are
/// runtime-only by construction.
/// </para>
/// <para>
/// Orthogonal to <see cref="BotNexus.Domain.Primitives.SubAgentArchetype"/>, which
/// describes a sub-agent's <em>role</em> (researcher / coder / reviewer / …). A
/// descriptor with <c>Kind == SubAgent</c> always carries an archetype on its
/// originating <c>SubAgentSpawnRequest</c>; the archetype lives on the spawn info, not
/// on the descriptor.
/// </para>
/// <para>
/// JSON serialization is locked to the STRING form only — the converter rejects
/// integer payloads (e.g. <c>"kind": 1</c>) so future enum-value reordering cannot
/// silently mutate persisted descriptors, and so a payload that supplies an
/// out-of-range integer (e.g. <c>"kind": 99</c>) cannot smuggle past the
/// <c>Kind == SubAgent</c> rejection guards in <c>AgentsController</c> /
/// <c>AgentDescriptorValidator.ValidateForConfig</c>.
/// </para>
/// </remarks>
[JsonConverter(typeof(AgentKindJsonConverter))]
public enum AgentKind
{
    /// <summary>
    /// A first-class agent registered through configuration or the REST API. The default
    /// for any descriptor that does not explicitly opt in to a different kind.
    /// </summary>
    Named = 0,

    /// <summary>
    /// A runtime-spawned ephemeral sub-agent created by another agent via
    /// <c>spawn_subagent</c>. Sub-agents inherit the parent agent's deny-list, cannot
    /// recursively spawn further sub-agents, and have no configuration persistence.
    /// </summary>
    SubAgent = 1,
}

/// <summary>
/// Strict JSON converter for <see cref="AgentKind"/>. Accepts only the string form
/// (<c>"Named"</c> / <c>"SubAgent"</c>, case-insensitive); rejects integer payloads,
/// out-of-range strings, and out-of-range integers. This is the defence that makes
/// the rejection of <c>Kind = SubAgent</c> in <c>AgentDescriptorValidator</c> /
/// <c>AgentsController</c> actually work — without it, a payload of
/// <c>"kind": 99</c> would deserialize to an undefined enum value and slip past the
/// equality check.
/// </summary>
internal sealed class AgentKindJsonConverter : JsonConverter<AgentKind>
{
    public override AgentKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"AgentKind must be serialized as a JSON string ('Named' or 'SubAgent'); " +
                $"got token type '{reader.TokenType}'. Integer values are intentionally rejected " +
                $"to prevent enum-value reordering and out-of-range smuggling.");
        }

        var raw = reader.GetString();
        if (raw is null)
        {
            throw new JsonException("AgentKind cannot be null.");
        }

        if (!Enum.TryParse<AgentKind>(raw, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new JsonException(
                $"AgentKind '{raw}' is not a recognised value. Allowed values: " +
                $"{string.Join(", ", Enum.GetNames<AgentKind>())}.");
        }

        return parsed;
    }

    public override void Write(Utf8JsonWriter writer, AgentKind value, JsonSerializerOptions options)
    {
        if (!Enum.IsDefined(value))
        {
            throw new JsonException(
                $"AgentKind value '{(int)value}' is not defined. Refusing to serialize an " +
                $"out-of-range value to avoid producing a payload that would fail strict " +
                $"deserialization on the other end.");
        }
        writer.WriteStringValue(value.ToString());
    }
}
