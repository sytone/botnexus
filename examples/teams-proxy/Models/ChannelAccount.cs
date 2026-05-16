using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.TeamsProxy.Models;

public sealed class ChannelAccount
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? AadObjectId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
