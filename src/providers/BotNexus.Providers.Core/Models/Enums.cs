using System.Text.Json.Serialization;

namespace BotNexus.Providers.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<StopReason>))]
public enum StopReason
{
    [JsonStringEnumMemberName("stop")] Stop,
    [JsonStringEnumMemberName("length")] Length,
    [JsonStringEnumMemberName("toolUse")] ToolUse,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("aborted")] Aborted,
    [JsonStringEnumMemberName("refusal")] Refusal,
    [JsonStringEnumMemberName("pause_turn")] PauseTurn,
    [JsonStringEnumMemberName("sensitive")] Sensitive
}

[JsonConverter(typeof(JsonStringEnumConverter<ThinkingLevel>))]
public enum ThinkingLevel
{
    [JsonStringEnumMemberName("minimal")] Minimal,
    [JsonStringEnumMemberName("low")] Low,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("xhigh")] ExtraHigh
}

[JsonConverter(typeof(JsonStringEnumConverter<CacheRetention>))]
public enum CacheRetention
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("short")] Short,
    [JsonStringEnumMemberName("long")] Long
}

[JsonConverter(typeof(JsonStringEnumConverter<Transport>))]
public enum Transport
{
    [JsonStringEnumMemberName("sse")] Sse,
    [JsonStringEnumMemberName("websocket")] WebSocket,
    [JsonStringEnumMemberName("auto")] Auto
}
