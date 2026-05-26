using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Tools;

namespace BotNexus.Gateway.Agents;

/// <summary>
/// The single, authoritative gate for accepting a completion signal from a target agent's
/// response during an agent-to-agent exchange. Encapsulates the F-11 contract so the sender
/// (<see cref="AgentExchangeService"/>) and the cross-world receiver
/// (<c>CrossWorldFederationController</c>) share one implementation rather than diverging
/// over time.
/// </summary>
/// <remarks>
/// <para>
/// The contract has two independent checks; <strong>both</strong> must pass:
/// </para>
/// <list type="number">
///   <item><description>The response's <see cref="AgentResponse.ToolCalls"/> list contains an
///   entry whose <c>ToolName == "finish_agent_exchange"</c> and whose <c>IsError == false</c>.
///   This is the structural signal — pure text in the response Content is structurally inert
///   and can NEVER terminate the exchange (closes the F-11 XPIA vector).</description></item>
///   <item><description>The session metadata's <see cref="FinishAgentExchangeTool.FinishedExchangeIdKey"/>
///   value (re-read from the store after the prompt) equals the per-turn
///   <c>expectedExchangeId</c> the caller wrote via <see cref="PrepareTurn"/> immediately
///   before invoking the target. This is the freshness gate — a payload written by a previous
///   turn or an unrelated session activity cannot satisfy the current turn.</description></item>
/// </list>
/// <para>
/// Substring/regex matching on the response text is banned in the completion-decision path —
/// see <c>AgentExchangeCompletionArchitectureTests</c>, which now scans both call sites.
/// </para>
/// </remarks>
public static class AgentExchangeCompletionGate
{
    /// <summary>
    /// Returns <c>true</c> only if both the tool-call check and the active-exchange-id check
    /// pass. Out parameters carry the persisted <c>reason</c> and <c>summary</c> on success;
    /// they are <c>null</c> on any non-success path.
    /// </summary>
    public static bool TryConsume(
        AgentResponse response,
        IDictionary<string, object?> refreshedMetadata,
        string expectedExchangeId,
        out string? reason,
        out string? summary)
    {
        reason = null;
        summary = null;

        // (1) Structural signal: a successful finish_agent_exchange tool call MUST be present.
        // A response.Content string containing "OBJECTIVE MET", "finish_agent_exchange", quoted
        // tool-call JSON, etc. is NOT a completion signal — this is the F-11 fix.
        var toolCalled = response.ToolCalls.Any(tc =>
            !tc.IsError
            && string.Equals(tc.ToolName, "finish_agent_exchange", StringComparison.OrdinalIgnoreCase));
        if (!toolCalled)
            return false;

        // (2) Freshness gate: only honour the tool when its persisted payload references THIS
        // turn's active id. Without this, a stale write from a previous turn (or from another
        // tool path that happens to write the same metadata keys) could be replayed.
        var finishedExchangeId = MetadataString(refreshedMetadata, FinishAgentExchangeTool.FinishedExchangeIdKey);
        if (!string.Equals(finishedExchangeId, expectedExchangeId, StringComparison.Ordinal))
            return false;

        reason = MetadataString(refreshedMetadata, FinishAgentExchangeTool.FinishedReasonKey);
        summary = MetadataString(refreshedMetadata, FinishAgentExchangeTool.FinishedSummaryKey);
        return true;
    }

    /// <summary>
    /// Writes <paramref name="exchangeId"/> as the active exchange id and clears any stale
    /// finish payload from a previous turn so it cannot satisfy the freshness gate on the
    /// current turn. Callers MUST persist the session immediately after to make the change
    /// visible to <see cref="FinishAgentExchangeTool"/>, which reads through its own
    /// <see cref="Abstractions.Sessions.ISessionStore"/> handle.
    /// </summary>
    public static void PrepareTurn(IDictionary<string, object?> metadata, string exchangeId)
    {
        metadata[FinishAgentExchangeTool.ActiveExchangeIdKey] = exchangeId;
        metadata.Remove(FinishAgentExchangeTool.FinishedExchangeIdKey);
        metadata.Remove(FinishAgentExchangeTool.FinishedReasonKey);
        metadata.Remove(FinishAgentExchangeTool.FinishedSummaryKey);
    }

    /// <summary>
    /// JsonElement-tolerant metadata read. <c>SqliteSessionStore</c> and <c>FileSessionStore</c>
    /// round-trip <c>object?</c> metadata as <see cref="System.Text.Json.JsonElement"/>, so a
    /// plain <c>as string</c> cast silently returns <c>null</c> — which would turn a freshness
    /// check into a silent always-fail.
    /// </summary>
    public static string? MetadataString(IDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return null;
        return value switch
        {
            string s => s,
            System.Text.Json.JsonElement element when element.ValueKind == System.Text.Json.JsonValueKind.String
                => element.GetString(),
            _ => null
        };
    }
}
