using System.Text.Json.Serialization;

namespace BotNexus.Extensions.Plugins.Lifecycle;

/// <summary>The authoritative manifest format selected during plugin import.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PluginManifestFormat>))]
public enum PluginManifestFormat
{
    /// <summary>The strict BotNexus manifest, also the default for older install records.</summary>
    Native,

    /// <summary>A Claude manifest accepted by the bounded conventional-skills adapter.</summary>
    Claude,
}
