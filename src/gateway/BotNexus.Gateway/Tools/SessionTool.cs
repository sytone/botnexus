using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Domain.Primitives;
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

    public Tool Definition => new(
        Name,
        "List, inspect, and search past conversation sessions. Use to recall previous discussions.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["list", "get", "search", "history"],
                  "description": "Action to perform."
                },
                "sessionId": {
                  "type": "string",
                  "description": "Session ID (for get/history actions)."
                },
                "agentId": {
                  "type": "string",
                  "description": "Filter by agent ID (for list action). Defaults to your own agent."
                },
                "query": {
                  "type": "string",
                  "description": "Search term to find in session messages (for search action)."
                },
                "offset": {
                  "type": "integer",
                  "description": "Pagination offset (for history action). Default: 0."
                },
                "limit": {
                  "type": "integer",
                  "description": "Max results to return. Default: 20."
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
            !action.Equals("history", StringComparison.OrdinalIgnoreCase))
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
            _ => throw new InvalidOperationException($"Unsupported session action '{action}'.")
        };
    }

    private async Task<AgentToolResult> ListAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct)
    {
        var filterAgentId = ReadString(arguments, "agentId") is { } requestedAgentId
            ? AgentId.From(requestedAgentId)
            : agentId;
        EnsureCanAccess(filterAgentId);

        var sessions = await sessionStore.ListAsync(filterAgentId, ct);
        var summaries = sessions.Select(s => new
        {
            s.SessionId,
            s.AgentId,
            s.ChannelType,
            s.CreatedAt,
            s.UpdatedAt,
            s.Status,
            s.MessageCount
        });

        return TextResult(JsonSerializer.Serialize(summaries, JsonOptions));
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
        var limit = ReadInt(arguments, "limit", 20);

        // Search across accessible sessions
        var targetAgentId = ReadString(arguments, "agentId");
        IReadOnlyList<Abstractions.Models.GatewaySession> sessions;

        if (accessLevel == SessionAccessLevel.All)
            sessions = await sessionStore.ListAsync(targetAgentId is null ? (AgentId?)null : AgentId.From(targetAgentId), ct);
        else if (accessLevel == SessionAccessLevel.Allowlist && targetAgentId is not null)
        {
            var typedTargetAgentId = AgentId.From(targetAgentId);
            EnsureCanAccess(typedTargetAgentId);
            sessions = await sessionStore.ListAsync(typedTargetAgentId, ct);
        }
        else
            sessions = await sessionStore.ListAsync(agentId, ct);

        var results = new List<object>();
        var queryLower = query.ToLowerInvariant();

        foreach (var session in sessions)
        {
            if (results.Count >= limit) break;

            var history = session.GetHistorySnapshot();
            var matches = history
                .Where(e => e.Content.Contains(queryLower, StringComparison.OrdinalIgnoreCase))
                .Select(e => new { e.Role, ContentPreview = Truncate(e.Content, 200), e.Timestamp })
                .Take(3)
                .ToList();

            if (matches.Count > 0)
            {
                results.Add(new
                {
                    session.SessionId,
                    session.AgentId,
                    session.ChannelType,
                    session.CreatedAt,
                    MatchCount = matches.Count,
                    Matches = matches
                });
            }
        }

        return TextResult(JsonSerializer.Serialize(results, JsonOptions));
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

    private void EnsureCanAccess(AgentId targetAgentId)
    {
        if (accessLevel == SessionAccessLevel.All)
            return;

        if (string.Equals(targetAgentId, agentId, StringComparison.OrdinalIgnoreCase))
            return;

        if (accessLevel == SessionAccessLevel.Allowlist &&
            allowedAgents is not null &&
            allowedAgents.Any(a => string.Equals(a, targetAgentId, StringComparison.OrdinalIgnoreCase)))
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
        => text.Length <= maxLength ? text : text[..maxLength] + "...";

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
