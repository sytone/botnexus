using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.Extensions.GitHub;

/// <summary>
/// The single serializer configuration used for both GitHub request bodies and tool results.
/// </summary>
/// <remarks>
/// One shared instance rather than per-call <c>new JsonSerializerOptions()</c>: System.Text.Json
/// caches its metadata per options instance, so constructing options inside a hot path silently
/// discards that cache. It also guarantees every tool in this extension emits the same shape.
/// </remarks>
internal static class GitHubJson
{
    /// <summary>Options for bodies sent to GitHub; null properties are omitted.</summary>
    internal static readonly JsonSerializerOptions RequestOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Options for structured tool results returned to the agent.</summary>
    internal static readonly JsonSerializerOptions ResultOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
