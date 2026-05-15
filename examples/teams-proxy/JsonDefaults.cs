using System.Text.Json;
using System.Text.Json.Serialization;

namespace BotNexus.TeamsProxy;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}
