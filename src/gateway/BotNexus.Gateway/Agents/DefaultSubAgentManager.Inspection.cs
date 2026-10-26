using System.Text.Json;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Sessions;

namespace BotNexus.Gateway.Agents;

public sealed partial class DefaultSubAgentManager
{
    private const int InspectionTextLimit = 2048;
    private const int InspectionEntryLimit = 20;
    private static readonly JsonSerializerOptions InspectionJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <inheritdoc />
    public async Task<string> InspectAsync(string subAgentId, SessionId parentSessionId, string action,
        int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAgentId);
        if (offset < 0 || limit < 1) throw new ArgumentException("Inspection paging requires offset >= 0 and limit >= 1.");
        if (action is not ("result" or "history")) throw new ArgumentException("Inspection supports result or history only.");
        if (_sessionStore is null) throw new NotSupportedException("Retained inspection requires a session store.");

        // The immutable durable admission is the sole grant: no ID prefix, broad session access,
        // live cache, conversation membership, or rollover can extend it to another session.
        var retained = await _sessionStore.GetSubAgentSessionAsync(subAgentId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Retained sub-agent was not found.");
        if (!string.Equals(retained.SubAgentId, subAgentId, StringComparison.Ordinal)
            || !string.Equals(retained.ParentSessionId, parentSessionId.Value, StringComparison.Ordinal)
            || string.Equals(retained.ChildSessionId, parentSessionId.Value, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Inspection is restricted to the exact original parent session, including after rollover.");

        if (action == "result")
        {
            if (!SubAgentStatusPolicy.IsTerminal(retained.Status))
                throw new InvalidOperationException("Result inspection requires a terminal retained run; use manage_subagent action=wait before reading final pages.");
            var full = await _sessionStore.GetSubAgentResultSummaryAsync(subAgentId, ct).ConfigureAwait(false);
            var available = full is not null;
            var safe = Scrub(full ?? retained.ResultSummary ?? "");
            var start = Math.Min(offset, safe.Length);
            var count = Math.Min(InspectionTextLimit, safe.Length - start);
            var next = start + count;
            return JsonSerializer.Serialize(new
            {
                subAgentId, status = retained.Status.ToString(), content = safe.Substring(start, count), offset,
                originalLength = available ? (int?)safe.Length : retained.OriginalLength,
                summaryTruncated = (!available && retained.SummaryTruncated) || start > 0 || next < safe.Length,
                nextOffset = next < safe.Length ? (int?)next : null, resultReference = subAgentId,
                fullResultAvailable = available,
                warning = available ? null : "Legacy record: only the retained summary is available; omitted original content cannot be recovered."
            }, InspectionJson);
        }

        if (retained.ChildSessionId is null) throw new InvalidOperationException("Retained run has no child session reference.");
        var child = await _sessionStore.GetAsync(SessionId.From(retained.ChildSessionId), ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Retained child history is unavailable.");
        if ((retained.ChildAgentId is not null && child.AgentId.Value != retained.ChildAgentId)
            || (retained.ChildConversationId is not null && child.ConversationId.Value != retained.ChildConversationId))
            throw new UnauthorizedAccessException("Retained child session identity has changed.");

        // Whitelist actual tool rows only. Never serialize SessionEntry: it also carries hidden
        // reasoning, system messages, provider metadata and media. Redact before bounding text so
        // a credential crossing a page boundary cannot leak a partial token.
        var evidence = child.GetHistorySnapshot().Where(e => e.Role == MessageRole.Tool
            && (e.Kind == MessageKind.ToolStart || e.Kind == MessageKind.ToolResult)).ToArray();
        var boundedLimit = Math.Min(limit, InspectionEntryLimit);
        var entries = evidence.Skip(offset).Take(boundedLimit).Select(e =>
        {
            var content = Scrub(e.Content);
            var args = Scrub(e.ToolArgs ?? "");
            return new
            {
                kind = e.Kind?.Value, toolName = Bound(Scrub(e.ToolName ?? "")), toolCallId = Bound(Scrub(e.ToolCallId ?? "")),
                e.Timestamp, content = Bound(content), contentTruncated = content.Length > InspectionTextLimit,
                originalLength = content.Length, arguments = Bound(args), argumentsTruncated = args.Length > InspectionTextLimit,
                e.ToolIsError, e.ToolIsIncomplete
            };
        }).ToArray();
        var end = (long)offset + entries.Length;
        return JsonSerializer.Serialize(new
        {
            subAgentId, offset, limit = boundedLimit, totalCount = evidence.Length, entries,
            nextOffset = end < evidence.Length ? (int?)end : null
        }, InspectionJson);
    }

    // Reapply today's configured rules even to persisted history written under older rules.
    // Keep the conservative transcript patterns as defense in depth; no text is bounded first.
    private string Scrub(string value) => _secretRedactor.Redact(TranscriptSecretRedactor.Redact(value) ?? "");
    private static string Bound(string value) => value[..Math.Min(value.Length, InspectionTextLimit)];
}
