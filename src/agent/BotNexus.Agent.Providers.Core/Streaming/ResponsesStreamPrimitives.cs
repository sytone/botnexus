using System.Text;
using System.Text.Json;
using BotNexus.Agent.Providers.Core.Models;
using BotNexus.Agent.Providers.Core.Registry;

namespace BotNexus.Agent.Providers.Core.Streaming;

/// <summary>
/// A single Server-Sent Events frame: the <c>event:</c> dispatch key and its <c>data:</c> payload.
/// Shared by the OpenAI and Copilot Responses stream parsers, which both parse the identical
/// Responses-API wire shape. Promoted to Providers.Core (step 5/6 of #1377) so the leaf type is
/// defined once rather than duplicated verbatim in each parser.
/// </summary>
public sealed record SseEvent(string Event, string Data);

/// <summary>
/// A transport-neutral Responses API event. Provider-private SSE and WebSocket adapters project
/// their frames into this shape before semantic normalization.
/// </summary>
public sealed record ResponsesEvent(string Event, string Data);

/// <summary>
/// Mutable accumulator for a single in-flight Responses-API tool call while its argument deltas
/// stream in. Both Responses parsers build the final <c>ToolCallContent</c> from this state, so it
/// lives once in Providers.Core (step 5/6 of #1377).
/// </summary>
public sealed class ToolState(string callId, string? itemId, string name, int contentIndex)
{
    /// <summary>The provider-assigned tool-call id (the <c>call_id</c> field).</summary>
    public string CallId { get; } = callId;

    /// <summary>The Responses output-item id, used to disambiguate parallel calls. May be null.</summary>
    public string? ItemId { get; } = itemId;

    /// <summary>The tool/function name being invoked.</summary>
    public string Name { get; } = name;

    /// <summary>The content-block index this tool call occupies in the assistant message.</summary>
    public int ContentIndex { get; } = contentIndex;

    /// <summary>Accumulated raw JSON argument text, appended from streamed deltas.</summary>
    public StringBuilder Arguments { get; } = new();
}

/// <summary>
/// Pure helpers shared by the OpenAI and Copilot Responses stream parsers. These were byte-identical
/// copies in each parser; promoting them to Providers.Core (step 5/6 of #1377) removes the
/// duplication while keeping the parsers behaviorally identical. The
/// <c>CopilotResponsesProviderParityTests</c> guard the move by asserting both providers still emit
/// identical streaming output.
/// </summary>
public static class ResponsesStreamHelpers
{
    /// <summary>
    /// Composes the canonical tool-call id from the provider <paramref name="callId"/> and optional
    /// Responses output-item id. When an item id is present the two are joined with a <c>|</c> so the
    /// downstream tool-result correlation can recover both halves; otherwise the call id is used as-is.
    /// </summary>
    public static string ComposeToolCallId(string callId, string? itemId)
        => string.IsNullOrWhiteSpace(itemId) ? callId : $"{callId}|{itemId}";

    /// <summary>
    /// Projects a Responses-API <c>usage</c> object into the core <see cref="Usage"/> model, folding
    /// cache read/write tokens out of the billed input count and attaching computed cost for the model.
    /// </summary>
    public static Usage ParseUsage(JsonElement usageElement, LlmModel model)
    {
        var inputTokens = usageElement.TryGetProperty("input_tokens", out var input) ? input.GetInt32() : 0;
        var outputTokens = usageElement.TryGetProperty("output_tokens", out var output) ? output.GetInt32() : 0;
        var total = usageElement.TryGetProperty("total_tokens", out var totalEl) ? totalEl.GetInt32() : inputTokens + outputTokens;
        var cacheRead = 0;
        var cacheWrite = 0;
        if (usageElement.TryGetProperty("input_tokens_details", out var details))
        {
            if (details.TryGetProperty("cached_tokens", out var cached))
                cacheRead = cached.GetInt32();
            if (details.TryGetProperty("cache_write_tokens", out var write))
                cacheWrite = write.GetInt32();
        }
        var usage = new Usage
        {
            Input = Math.Max(0, inputTokens - cacheRead - cacheWrite),
            Output = outputTokens,
            CacheRead = cacheRead,
            CacheWrite = cacheWrite,
            TotalTokens = total
        };
        return usage with { Cost = ModelRegistry.CalculateCost(model, usage) };
    }

    /// <summary>
    /// Maps a Responses-API completion <c>status</c> string to the core <see cref="StopReason"/>.
    /// Unknown, in-progress, or absent statuses fall back to <see cref="StopReason.Stop"/>.
    /// </summary>
    public static StopReason MapStopReason(string? status) => status switch
    {
        "completed" => StopReason.Stop,
        "incomplete" => StopReason.Length,
        "refusal" => StopReason.Refusal,
        "content_filter" => StopReason.Sensitive,
        "failed" => StopReason.Error,
        "cancelled" => StopReason.Error,
        "in_progress" => StopReason.Stop,
        "queued" => StopReason.Stop,
        _ => StopReason.Stop
    };
}
