using System.Text.Json.Serialization;

namespace BotNexus.Agent.Providers.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<StopReason>))]
/// <summary>
/// Specifies supported values for stop reason.
/// </summary>
public enum StopReason
{
    /// <summary>
    /// Normal completion of generation.
    /// Emitted by all providers.
    /// </summary>
    [JsonStringEnumMemberName("stop")] Stop,
    /// <summary>
    /// Generation stopped because the maximum token limit was reached.
    /// Emitted by all providers.
    /// </summary>
    [JsonStringEnumMemberName("length")] Length,
    /// <summary>
    /// Model requested tool execution during generation.
    /// Emitted by all providers when tool calls are returned.
    /// </summary>
    [JsonStringEnumMemberName("toolUse")] ToolUse,
    /// <summary>
    /// Provider or upstream API error occurred during generation.
    /// </summary>
    [JsonStringEnumMemberName("error")] Error,
    /// <summary>
    /// Request was cancelled by the user or caller before completion.
    /// </summary>
    [JsonStringEnumMemberName("aborted")] Aborted,
    /// <summary>
    /// Content policy refusal.
    /// Emitted by Anthropic and OpenAI safety filters.
    /// </summary>
    [JsonStringEnumMemberName("refusal")] Refusal,
    /// <summary>
    /// Azure AI content filtering blocked the response as sensitive.
    /// </summary>
    [JsonStringEnumMemberName("sensitive")] Sensitive
}

[JsonConverter(typeof(JsonStringEnumConverter<ThinkingLevel>))]
/// <summary>
/// Specifies supported values for thinking level.
/// </summary>
public enum ThinkingLevel
{
    [JsonStringEnumMemberName("minimal")] Minimal,
    [JsonStringEnumMemberName("low")] Low,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("xhigh")] ExtraHigh
}

[JsonConverter(typeof(JsonStringEnumConverter<CacheRetention>))]
/// <summary>
/// Specifies supported values for cache retention.
/// </summary>
public enum CacheRetention
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("short")] Short,
    [JsonStringEnumMemberName("long")] Long
}

[JsonConverter(typeof(JsonStringEnumConverter<Transport>))]
/// <summary>
/// Specifies supported values for transport.
/// </summary>
public enum Transport
{
    [JsonStringEnumMemberName("sse")] Sse,
    [JsonStringEnumMemberName("websocket")] WebSocket,
    [JsonStringEnumMemberName("auto")] Auto
}
