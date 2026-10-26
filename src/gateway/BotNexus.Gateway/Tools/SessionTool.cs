using BotNexus.Domain.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
using BotNexus.Gateway.Abstractions.Models;
using BotNexus.Gateway.Abstractions.Sessions;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Gateway.Tools;

/// <summary>
/// Agent tool for listing, inspecting, and searching session history.
/// Access is scoped per-agent based on <see cref="SessionAccessLevel"/>.
/// </summary>
public sealed class SessionTool(
    ISessionStore sessionStore,
    AgentId agentId,
    SessionAccessLevel accessLevel = SessionAccessLevel.Own,
    IReadOnlyList<string>? allowedAgents = null) : IAgentTool
{
    public string Name => "sessions";
    public string Label => "Session Manager";

    /// <summary>Content source classification for turn-taint accumulation (#2519). Gateway-owned session store.</summary>
    public string ContentSource => ToolContentSource.Local;

    public Tool Definition => new(
        Name,
        "List, inspect, and search past conversation sessions. Use to recall previous discussions.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["list", "get", "search", "history", "delete"],
                  "description": "Action to perform."
                },
                "sessionId": {
                  "type": "string",
                  "description": "Session ID (for get/history actions)."
                },
                "agentId": {
                  "type": "string",
                  "description": "Filter by agent ID for list/search. Defaults to your own agent; All access search without a filter searches all agents. Allowlist search requires an explicit target to search a peer."
                },
                "query": {
                  "type": "string",
                  "description": "Search term to find in session messages (for search action)."
                },
                "offset": {
                  "type": "integer",
                  "description": "Non-negative Int32 offset. List/search page candidate sessions newest first, ID tie-break. Search scans at most 100 sessions, 2000 entries and 1048576 characters; complete=false and incompleteSessions report unsearched content. Default: 0."
                },
                "limit": {
                  "type": "integer",
                  "description": "Max results to return. List/search: positive Int32, clamped to 100; invalid values rejected. Default: 20. List/search return items, hasMore and nextOffset; search also reports completeness. File legacy messageCount=-1 means unknown; orphan sidecars without canonical ownership are excluded."
                }
              },
              "required": ["action"]
            }
            """).RootElement.Clone());

    public Task<IReadOnlyDictionary<string, object?>> PrepareArgumentsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var action = ReadString(arguments, "action") ?? throw new ArgumentException("Missing required argument: action.");
        if (!action.Equals("list", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("get", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("search", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("history", StringComparison.OrdinalIgnoreCase) &&
            !action.Equals("delete", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported session action '{action}'.");

        return Task.FromResult(arguments);
    }

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = (ReadString(arguments, "action") ?? "").ToLowerInvariant();
        return action switch
        {
            "list" => await ListAsync(arguments, cancellationToken),
            "get" => await GetAsync(arguments, cancellationToken),
            "search" => await SearchAsync(arguments, cancellationToken),
            "history" => await HistoryAsync(arguments, cancellationToken),
            "delete" => await DeleteAsync(arguments, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported session action '{action}'.")
        };
    }

    private async Task<AgentToolResult> ListAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var filterAgentId = ReadString(arguments, "agentId") is { } requestedAgentId
            ? AgentId.From(requestedAgentId)
            : agentId;
        EnsureCanAccess(filterAgentId);

        var limit = ReadWindow(arguments, "limit", 20, 100);
        var offset = ReadWindow(arguments, "offset", 0, int.MaxValue, allowZero: true);
        ct.ThrowIfCancellationRequested();
        var page = await sessionStore.ListSummaryPageAsync(new SessionSummaryQuery(
            DateTimeOffset.MinValue, AgentId: filterAgentId.Value, IncludeInactive: true,
            Limit: limit, Offset: offset, MetadataOnly: true), ct);
        var items = page.Items.Select(s => new
        {
            s.SessionId, s.AgentId, s.ChannelType, s.CreatedAt, s.UpdatedAt, s.Status, s.MessageCount
        });
        return TextResult(JsonSerializer.Serialize(new
        {
            Items = items, Offset = offset, Limit = limit, page.TotalCount, page.HasMore,
            NextOffset = page.HasMore ? (long?)offset + page.Items.Count : null
        }, JsonOptions));
    }

    private async Task<AgentToolResult> GetAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var sessionId = ReadString(arguments, "sessionId")
            ?? throw new ArgumentException("Missing required argument: sessionId.");

        var session = await sessionStore.GetAsync(SessionId.From(sessionId), ct)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        EnsureCanAccess(session.AgentId);

        var summary = new
        {
            session.SessionId,
            session.AgentId,
            session.ChannelType,
            session.CreatedAt,
            session.UpdatedAt,
            session.Status,
            session.MessageCount,
            session.Metadata
        };

        return TextResult(JsonSerializer.Serialize(summary, JsonOptions));
    }

    private async Task<AgentToolResult> SearchAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var query = ReadString(arguments, "query")
            ?? throw new ArgumentException("Missing required argument: query.");
        var limit = ReadWindow(arguments, "limit", 20, 100);
        var offset = ReadWindow(arguments, "offset", 0, int.MaxValue, allowZero: true);
        if (query.Length is 0 or > 2000)
            throw new ArgumentException("Search query must contain 1..2000 characters.");
        ct.ThrowIfCancellationRequested();
        var requested = ReadString(arguments, "agentId");
        AgentId? target = requested is null
            ? (accessLevel == SessionAccessLevel.All ? null : agentId)
            : AgentId.From(requested);
        if (target is { } owner) EnsureCanAccess(owner);

        // Offset addresses candidate sessions, not matching sessions. This lets callers advance
        // across sparse corpora without rescanning prefixes. A partial session is explicitly named.
        const int candidateBudget = 100;
        const int entryBudget = 2000;
        const int characterBudget = 1_048_576;
        var page = await sessionStore.ListSummaryPageAsync(new SessionSummaryQuery(
            DateTimeOffset.MinValue, AgentId: target?.Value, IncludeInactive: true,
            Limit: candidateBudget, Offset: offset, MetadataOnly: true), ct);
        var results = new List<object>();
        var scannedEntries = 0;
        var scannedCharacters = 0;
        var scannedSessions = 0;
        var incompleteSessions = new List<string>();
        foreach (var session in page.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (results.Count >= limit || scannedEntries >= entryBudget || scannedCharacters >= characterBudget)
                break;
            var ownerId = AgentId.From(session.AgentId);
            EnsureCanAccess(ownerId);
            var history = await sessionStore.ScanHistoryAsync(SessionId.From(session.SessionId),
                new SessionHistoryScan(ownerId, entryBudget - scannedEntries, characterBudget - scannedCharacters), ct);
            scannedSessions++;
            scannedEntries += history.Entries.Count;
            scannedCharacters += history.MaterializedCharacters;
            if (!history.Complete) incompleteSessions.Add(session.SessionId);
            var matches = new List<object>();
            foreach (var entry in history.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (!entry.Content.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add(new { entry.Role, ContentPreview = Truncate(entry.Content, 200), entry.Timestamp });
                if (matches.Count == 3) break;
            }
            if (matches.Count > 0)
                results.Add(new { session.SessionId, session.AgentId, session.ChannelType, session.CreatedAt,
                    MatchCount = matches.Count, Matches = matches });
            // A giant or entry-heavy transcript consumes this request's scan, not the rest of the corpus.
            if (!history.Complete) break;
        }
        var hasMore = page.HasMore || scannedSessions < page.Items.Count;
        return TextResult(JsonSerializer.Serialize(new
        {
            Items = results, Offset = offset, Limit = limit, HasMore = hasMore,
            NextOffset = hasMore ? (long?)offset + scannedSessions : null,
            Complete = !hasMore && incompleteSessions.Count == 0,
            ScannedSessions = scannedSessions, ScannedEntries = scannedEntries,
            ScannedCharacters = scannedCharacters, IncompleteSessions = incompleteSessions
        }, JsonOptions));
    }

    private async Task<AgentToolResult> HistoryAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var sessionId = ReadString(arguments, "sessionId")
            ?? throw new ArgumentException("Missing required argument: sessionId.");
        var offset = ReadInt(arguments, "offset", 0);
        var limit = ReadInt(arguments, "limit", 20);

        var session = await sessionStore.GetAsync(SessionId.From(sessionId), ct)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        EnsureCanAccess(session.AgentId);

        var entries = session.GetHistorySnapshot(offset, Math.Min(limit, 100));
        var result = new
        {
            session.SessionId,
            session.AgentId,
            Offset = offset,
            Limit = limit,
            TotalCount = session.MessageCount,
            Entries = entries.Select(e => new { e.Role, e.Content, e.Timestamp, e.ToolName })
        };

        return TextResult(JsonSerializer.Serialize(result, JsonOptions));
    }

    private async Task<AgentToolResult> DeleteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var sessionId = ReadString(arguments, "sessionId")
            ?? throw new ArgumentException("Missing required argument: sessionId.");

        var session = await sessionStore.GetAsync(SessionId.From(sessionId), ct)
            ?? throw new KeyNotFoundException($"Session '{sessionId}' not found.");

        EnsureCanAccess(session.AgentId);

        if (session.Status is SessionStatus.Active or SessionStatus.Suspended)
            throw new InvalidOperationException(
                $"Cannot delete session '{sessionId}' with status '{session.Status}'. Only expired or sealed sessions can be deleted.");

        await sessionStore.DeleteAsync(SessionId.From(sessionId), ct);

        var confirmation = new
        {
            status = "deleted",
            session.SessionId,
            session.AgentId,
            session.CreatedAt
        };

        return TextResult(JsonSerializer.Serialize(confirmation, JsonOptions));
    }

    private void EnsureCanAccess(AgentId targetAgentId)
    {
        if (accessLevel == SessionAccessLevel.All)
            return;

        if (string.Equals(targetAgentId.Value, agentId.Value, StringComparison.OrdinalIgnoreCase))
            return;

        if (accessLevel == SessionAccessLevel.Allowlist &&
            allowedAgents is not null &&
            allowedAgents.Any(a => string.Equals(a, targetAgentId.Value, StringComparison.OrdinalIgnoreCase)))
            return;

        throw new UnauthorizedAccessException($"Agent '{agentId}' does not have access to sessions for agent '{targetAgentId}'.");
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return null;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            JsonElement el => el.ToString(),
            _ => value.ToString()
        };
    }

    // Reject malformed, fractional, negative and overflowing values; only a valid positive
    // Int32 limit is clamped. Offsets are candidate coordinates and may include zero.
    private static int ReadWindow(IReadOnlyDictionary<string, object?> args, string key,
        int defaultValue, int maximum, bool allowZero = false)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return defaultValue;
        var text = value is JsonElement element ? element.ToString() : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        if (!int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            || number < (allowZero ? 0 : 1))
            throw new ArgumentException($"{key} must be a {(allowZero ? "non-negative" : "positive")} Int32 integer.");
        return Math.Min(number, maximum);
    }

    private static int ReadInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
            return defaultValue;

        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetInt32(out var i) => i,
            JsonElement { ValueKind: JsonValueKind.Number } element => (int)element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } el when int.TryParse(el.GetString(), out var i) => i,
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(element.GetString(), out var d) => (int)d,
            int i => i,
            double d => (int)d,
            string s when int.TryParse(s, out var i) => i,
            _ => defaultValue
        };
    }

    private static string Truncate(string text, int maxLength)
        => TextTruncation.SafeTruncate(text, maxLength, "...")!;

    private static AgentToolResult TextResult(string text)
        => new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>Session access level for the session tool.</summary>
public enum SessionAccessLevel
{
    /// <summary>Agent can only access its own sessions.</summary>
    Own,
    /// <summary>Agent can access its own sessions plus those of explicitly allowed agents.</summary>
    Allowlist,
    /// <summary>Agent can access all sessions.</summary>
    All
}
