using System.Text.Json.Serialization;

namespace BotNexus.Domain.World;

/// <summary>
/// Discriminates the species of a <see cref="CitizenId"/>. Citizens of a BotNexus world
/// are either users (humans) or agents.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is the explicit default value so that <c>default(CitizenId)</c>
/// is unambiguously invalid (rather than masquerading as a malformed user). Code that
/// receives a <c>CitizenId</c> can assert <c>Kind != CitizenKind.Unknown</c> to detect
/// uninitialised inputs.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CitizenKind>))]
public enum CitizenKind
{
    /// <summary>Sentinel value reserved for <c>default(CitizenId)</c>; never a valid citizen.</summary>
    Unknown = 0,

    /// <summary>The citizen is a human user.</summary>
    User = 1,

    /// <summary>The citizen is a named agent.</summary>
    Agent = 2,
}
