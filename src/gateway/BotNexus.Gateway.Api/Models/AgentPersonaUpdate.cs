using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Api.Models;

/// <summary>
/// The persona subset of an agent, as the quick-edit surfaces send it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because <c>PUT /api/agents/{id}</c> binds a whole <c>AgentDescriptor</c> and
/// full-replaces it: every descriptor property the caller does not model binds as its default and
/// is then REMOVED from config.json by the writer. A fast-edit panel that sent only a persona body
/// would therefore blank <c>ToolIds</c>, <c>Memory</c>, <c>Heartbeat</c> and the rest — and would
/// not even get that far, because <c>AgentId</c>, <c>DisplayName</c>, <c>ModelId</c> and
/// <c>ApiProvider</c> are <c>required</c> members and the validator also demands
/// <c>IsolationStrategy</c>.
/// </para>
/// <para>
/// So the persona is edited through its own route, which reads the live descriptor and applies
/// <c>with { }</c> for exactly these six fields. Nothing else can be reached from here, which is
/// the point: it is a narrow write, not a descriptor replace wearing a smaller name.
/// </para>
/// <para>
/// Every field is sent on every save — this is a full replace OF THE PERSONA, not a patch — so
/// there is no "supplied vs. omitted" ambiguity to resolve. For the four optional text fields,
/// null or whitespace means "clear it"; for <see cref="AvatarHue"/>, null means "Auto", which is a
/// real user choice and not an absence. That is why the route is PUT rather than PATCH.
/// </para>
/// </remarks>
public sealed record AgentPersonaUpdate
{
    /// <summary>The agent's display name. The one persona field that may not be blank.</summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>An emoji that stands in for the agent, or null/blank to fall back to a monogram.</summary>
    [JsonPropertyName("emoji")]
    public string? Emoji { get; init; }

    /// <summary>
    /// Operator-chosen avatar hue in degrees, or null for "Auto" — generate one from the agent id.
    /// Null is a real choice here, and 0 is a real hue (red), never a sentinel for unset.
    /// </summary>
    [JsonPropertyName("avatarHue")]
    public int? AvatarHue { get; init; }

    /// <summary>One short line naming what this agent owns.</summary>
    [JsonPropertyName("responsibility")]
    public string? Responsibility { get; init; }

    /// <summary>Prose describing the work this agent does.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    /// <summary>What this agent must not do.</summary>
    [JsonPropertyName("boundaries")]
    public string? Boundaries { get; init; }
}
