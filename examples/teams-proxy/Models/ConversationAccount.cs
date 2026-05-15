using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.TeamsProxy.Models;

public sealed class ConversationAccount
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public bool? IsGroup { get; set; }

    public string? ConversationType { get; set; }

    public string? TenantId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
