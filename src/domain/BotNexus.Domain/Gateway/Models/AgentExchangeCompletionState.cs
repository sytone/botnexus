using System.Text.Json;

namespace BotNexus.Gateway.Abstractions.Models;

/// <summary>
/// Typed completion state for an agent-to-agent exchange, promoted from the four loose
/// <see cref="Session.Metadata"/> string keys (<c>activeAgentExchangeId</c>,
/// <c>finishedAgentExchangeId</c>, <c>finishedAgentExchangeReason</c>,
/// <c>finishedAgentExchangeSummary</c>) that previously carried this state as an ad-hoc bag
/// read/written through duplicate <c>MetadataString</c> JsonElement-coercion helpers
/// (issue #612, CC-1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Storage &amp; back-compat.</strong> This state is projected onto
/// <see cref="Session.Metadata"/> so it round-trips through the existing session-store
/// persistence (Sqlite / File / InMemory) without any schema change. New writes serialise the
/// whole state under the single canonical key <see cref="MetadataKey"/>; reads
/// <em>migrate-on-read</em>: a row that still carries the four legacy loose keys
/// (<see cref="LegacyActiveExchangeIdKey"/> et al.) is transparently surfaced as this typed
/// state, so pre-CC-1 persisted sessions keep working. Writes always clear the legacy loose
/// keys so a stale value can never shadow the canonical blob.
/// </para>
/// <para>
/// <strong>Freshness gate.</strong> <see cref="ActiveExchangeId"/> is written per-turn before
/// the target agent is prompted; <see cref="FinishedExchangeId"/> is written by
/// <c>FinishAgentExchangeTool</c> and must equal the active id for the completion signal to be
/// honoured (F-11). The single JsonElement-tolerant coercion here replaces the duplicated
/// helpers because <c>SqliteSessionStore</c> and <c>FileSessionStore</c> round-trip
/// <c>object?</c> metadata as <see cref="JsonElement"/>.
/// </para>
/// </remarks>
public sealed record AgentExchangeCompletionState
{
    /// <summary>The active exchange id the service writes before each turn (null when no turn is in flight).</summary>
    public string? ActiveExchangeId { get; init; }

    /// <summary>The exchange id the finish tool stamped when fired; must match <see cref="ActiveExchangeId"/> to be honoured.</summary>
    public string? FinishedExchangeId { get; init; }

    /// <summary>Caller-supplied completion reason.</summary>
    public string? FinishedReason { get; init; }

    /// <summary>Optional caller-supplied completion summary.</summary>
    public string? FinishedSummary { get; init; }

    /// <summary>Canonical metadata key the whole typed state is serialised under for new writes.</summary>
    public const string MetadataKey = "agentExchangeCompletion";

    /// <summary>Legacy loose metadata key for the active exchange id (migrate-on-read only).</summary>
    public const string LegacyActiveExchangeIdKey = "activeAgentExchangeId";

    /// <summary>Legacy loose metadata key for the finished exchange id (migrate-on-read only).</summary>
    public const string LegacyFinishedExchangeIdKey = "finishedAgentExchangeId";

    /// <summary>Legacy loose metadata key for the finished reason (migrate-on-read only).</summary>
    public const string LegacyFinishedReasonKey = "finishedAgentExchangeReason";

    /// <summary>Legacy loose metadata key for the finished summary (migrate-on-read only).</summary>
    public const string LegacyFinishedSummaryKey = "finishedAgentExchangeSummary";

    /// <summary>True when no field carries a value - such a state is never persisted.</summary>
    public bool IsEmpty =>
        ActiveExchangeId is null
        && FinishedExchangeId is null
        && FinishedReason is null
        && FinishedSummary is null;

    /// <summary>
    /// Projects the completion state out of <paramref name="metadata"/>. Prefers the canonical
    /// <see cref="MetadataKey"/> blob; falls back to migrating the four legacy loose keys.
    /// Returns <c>null</c> when neither representation carries any value.
    /// </summary>
    public static AgentExchangeCompletionState? FromMetadata(IDictionary<string, object?> metadata)
    {
        if (metadata.TryGetValue(MetadataKey, out var raw) && Coerce(raw) is { IsEmpty: false } parsed)
            return parsed;

        var legacy = new AgentExchangeCompletionState
        {
            ActiveExchangeId = CoerceString(metadata, LegacyActiveExchangeIdKey),
            FinishedExchangeId = CoerceString(metadata, LegacyFinishedExchangeIdKey),
            FinishedReason = CoerceString(metadata, LegacyFinishedReasonKey),
            FinishedSummary = CoerceString(metadata, LegacyFinishedSummaryKey)
        };
        return legacy.IsEmpty ? null : legacy;
    }

    /// <summary>
    /// Writes <paramref name="state"/> onto <paramref name="metadata"/> under the canonical key,
    /// always removing the legacy loose keys first so they can never shadow or stale-replay. A
    /// <c>null</c> or empty state clears the canonical key too.
    /// </summary>
    public static void WriteTo(IDictionary<string, object?> metadata, AgentExchangeCompletionState? state)
    {
        metadata.Remove(LegacyActiveExchangeIdKey);
        metadata.Remove(LegacyFinishedExchangeIdKey);
        metadata.Remove(LegacyFinishedReasonKey);
        metadata.Remove(LegacyFinishedSummaryKey);

        if (state is null || state.IsEmpty)
            metadata.Remove(MetadataKey);
        else
            metadata[MetadataKey] = state;
    }

    private static AgentExchangeCompletionState? Coerce(object? raw) => raw switch
    {
        null => null,
        AgentExchangeCompletionState state => state,
        JsonElement { ValueKind: JsonValueKind.Object } element => new AgentExchangeCompletionState
        {
            ActiveExchangeId = GetProperty(element, "activeExchangeId"),
            FinishedExchangeId = GetProperty(element, "finishedExchangeId"),
            FinishedReason = GetProperty(element, "finishedReason"),
            FinishedSummary = GetProperty(element, "finishedSummary")
        },
        _ => null
    };

    private static string? GetProperty(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? CoerceString(IDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
    }
}
