using System.Text.Json.Serialization;

namespace BotNexus.Gateway.Abstractions.Agents;

/// <summary>
/// The selected reasoning effort reported by gateway diagnostics and runtime prompts. Null at the
/// use site means no layer selected a level; these values do not claim provider support.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExecutionThinkingLevel>))]
public enum ExecutionThinkingLevel
{
    /// <summary>Minimal reasoning effort.</summary>
    [JsonStringEnumMemberName("minimal")] Minimal = 0,
    /// <summary>Low reasoning effort.</summary>
    [JsonStringEnumMemberName("low")] Low = 1,
    /// <summary>Medium reasoning effort.</summary>
    [JsonStringEnumMemberName("medium")] Medium = 2,
    /// <summary>High reasoning effort.</summary>
    [JsonStringEnumMemberName("high")] High = 3,
    /// <summary>Extra-high reasoning effort, represented as xhigh on the wire.</summary>
    [JsonStringEnumMemberName("xhigh")] ExtraHigh = 4,
    /// <summary>Maximum reasoning effort.</summary>
    [JsonStringEnumMemberName("max")] Max = 5
}
