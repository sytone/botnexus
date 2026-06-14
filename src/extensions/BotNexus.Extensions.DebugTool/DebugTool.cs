using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;
using Microsoft.Data.Sqlite;

namespace BotNexus.Extensions.DebugTool;

/// <summary>
/// Agent-facing read-only debug tool for inspecting platform state (sessions.db and runtime).
/// All database access is read-only (Mode=ReadOnly). Queries are agent-scoped by default.
/// </summary>
public sealed class DebugTool : IAgentTool
{
    private readonly string _dbPath;
    private readonly string _agentId;
    private readonly DebugToolConfig _config;
    private readonly IRuntimeStateProvider? _runtimeStateProvider;

    internal const int DefaultLimit = 50;
    internal const int MaxLimit = 500;

    internal static readonly HashSet<string> ValidActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "query_sessions",
        "query_conversations",
        "query_history",
        "query_sub_agents",
        "session_info",
        "conversation_info",
        "runtime_status",
        "raw_sql"
    };

    public DebugTool(string dbPath, string agentId, DebugToolConfig config, IRuntimeStateProvider? runtimeStateProvider = null)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
        _agentId = agentId ?? throw new ArgumentNullException(nameof(agentId));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _runtimeStateProvider = runtimeStateProvider;
    }

    public string Name => "platform_debug";
    public string Label => "Platform Debug";

    public Tool Definition => new(
        Name,
        "Read-only inspection of platform sessions.db and gateway runtime state. Query sessions, conversations, history, sub-agents, or execute raw SELECT statements against the platform database.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["query_sessions", "query_conversations", "query_history", "query_sub_agents", "session_info", "conversation_info", "runtime_status", "raw_sql"],
                  "description": "Action to perform."
                },
                "session_id": {
                  "type": "string",
                  "description": "Session ID filter (for query_history, query_sub_agents, session_info)."
                },
                "conversation_id": {
                  "type": "string",
                  "description": "Conversation ID filter (for query_sessions, conversation_info)."
                },
                "agent_id": {
                  "type": "string",
                  "description": "Agent ID filter. Defaults to current agent when omitted."
                },
                "status": {
                  "type": "string",
                  "description": "Status filter (for query_sessions, query_conversations)."
                },
                "kind": {
                  "type": "string",
                  "description": "Kind filter (for query_conversations)."
                },
                "role": {
                  "type": "string",
                  "description": "Message role filter (for query_history)."
                },
                "after": {
                  "type": "string",
                  "description": "ISO date/time lower bound (inclusive) for date range filtering."
                },
                "before": {
                  "type": "string",
                  "description": "ISO date/time upper bound (exclusive) for date range filtering."
                },
                "sql": {
                  "type": "string",
                  "description": "SELECT statement for 'raw_sql' action. Only SELECT is permitted."
                },
                "offset": {
                  "type": "integer",
                  "description": "Pagination offset. Default: 0."
                },
                "limit": {
                  "type": "integer",
                  "description": "Maximum rows to return. Default: 50, max: 500."
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
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(
            new Dictionary<string, object?>(arguments, StringComparer.OrdinalIgnoreCase));
    }

    public Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = GetString(arguments, "action")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action) || !ValidActions.Contains(action))
            return Task.FromResult(TextResult($"Error: Unknown action '{action}'. Valid actions: {string.Join(", ", ValidActions.Order())}."));

        var result = action switch
        {
            "query_sessions" => QuerySessions(arguments),
            "query_conversations" => QueryConversations(arguments),
            "query_history" => QueryHistory(arguments),
            "query_sub_agents" => QuerySubAgents(arguments),
            "session_info" => SessionInfo(arguments),
            "conversation_info" => ConversationInfo(arguments),
            "runtime_status" => RuntimeStatus(),
            "raw_sql" => RawSql(arguments),
            _ => TextResult($"Error: Unhandled action '{action}'.")
        };

        return Task.FromResult(result);
    }

    // ── Action implementations ────────────────────────────────────────────────

    private AgentToolResult QuerySessions(IReadOnlyDictionary<string, object?> args)
    {
        var agentId = GetString(args, "agent_id") ?? _agentId;
        var conversationId = GetString(args, "conversation_id");
        var status = GetString(args, "status");
        var after = GetString(args, "after");
        var before = GetString(args, "before");
        var (offset, limit) = GetPagination(args);

        var sb = new StringBuilder("SELECT * FROM sessions WHERE 1=1");
        var parameters = new List<SqliteParameter>();

        sb.Append(" AND conversation_id IN (SELECT id FROM conversations WHERE agent_id = @agentId)");
        parameters.Add(new SqliteParameter("@agentId", agentId));

        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            sb.Append(" AND conversation_id = @conversationId");
            parameters.Add(new SqliteParameter("@conversationId", conversationId));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            sb.Append(" AND status = @status");
            parameters.Add(new SqliteParameter("@status", status));
        }

        if (!string.IsNullOrWhiteSpace(after))
        {
            sb.Append(" AND created_at >= @after");
            parameters.Add(new SqliteParameter("@after", after));
        }

        if (!string.IsNullOrWhiteSpace(before))
        {
            sb.Append(" AND created_at < @before");
            parameters.Add(new SqliteParameter("@before", before));
        }

        sb.Append(" ORDER BY created_at DESC");
        sb.Append($" LIMIT {limit} OFFSET {offset}");

        return ExecuteReadQuery(sb.ToString(), parameters);
    }

    private AgentToolResult QueryConversations(IReadOnlyDictionary<string, object?> args)
    {
        var agentId = GetString(args, "agent_id") ?? _agentId;
        var status = GetString(args, "status");
        var kind = GetString(args, "kind");
        var after = GetString(args, "after");
        var before = GetString(args, "before");
        var (offset, limit) = GetPagination(args);

        var sb = new StringBuilder("SELECT * FROM conversations WHERE agent_id = @agentId");
        var parameters = new List<SqliteParameter>
        {
            new("@agentId", agentId)
        };

        if (!string.IsNullOrWhiteSpace(status))
        {
            sb.Append(" AND status = @status");
            parameters.Add(new SqliteParameter("@status", status));
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            sb.Append(" AND kind = @kind");
            parameters.Add(new SqliteParameter("@kind", kind));
        }

        if (!string.IsNullOrWhiteSpace(after))
        {
            sb.Append(" AND created_at >= @after");
            parameters.Add(new SqliteParameter("@after", after));
        }

        if (!string.IsNullOrWhiteSpace(before))
        {
            sb.Append(" AND created_at < @before");
            parameters.Add(new SqliteParameter("@before", before));
        }

        sb.Append(" ORDER BY created_at DESC");
        sb.Append($" LIMIT {limit} OFFSET {offset}");

        return ExecuteReadQuery(sb.ToString(), parameters);
    }

    private AgentToolResult QueryHistory(IReadOnlyDictionary<string, object?> args)
    {
        var sessionId = GetString(args, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return TextResult("Error: 'session_id' is required for query_history.");

        var role = GetString(args, "role");
        var after = GetString(args, "after");
        var before = GetString(args, "before");
        var (offset, limit) = GetPagination(args);

        // Verify session belongs to this agent
        var agentId = GetString(args, "agent_id") ?? _agentId;
        var ownershipCheck = VerifySessionOwnership(sessionId, agentId);
        if (ownershipCheck is not null)
            return ownershipCheck;

        var sb = new StringBuilder("SELECT * FROM session_history WHERE session_id = @sessionId");
        var parameters = new List<SqliteParameter>
        {
            new("@sessionId", sessionId)
        };

        if (!string.IsNullOrWhiteSpace(role))
        {
            sb.Append(" AND role = @role");
            parameters.Add(new SqliteParameter("@role", role));
        }

        if (!string.IsNullOrWhiteSpace(after))
        {
            sb.Append(" AND timestamp >= @after");
            parameters.Add(new SqliteParameter("@after", after));
        }

        if (!string.IsNullOrWhiteSpace(before))
        {
            sb.Append(" AND timestamp < @before");
            parameters.Add(new SqliteParameter("@before", before));
        }

        sb.Append(" ORDER BY sequence_id ASC");
        sb.Append($" LIMIT {limit} OFFSET {offset}");

        return ExecuteReadQuery(sb.ToString(), parameters);
    }

    private AgentToolResult QuerySubAgents(IReadOnlyDictionary<string, object?> args)
    {
        var sessionId = GetString(args, "session_id");
        var agentId = GetString(args, "agent_id") ?? _agentId;
        var (offset, limit) = GetPagination(args);

        var sb = new StringBuilder("SELECT * FROM sub_agent_sessions WHERE 1=1");
        var parameters = new List<SqliteParameter>();

        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            // Verify session belongs to this agent
            var ownershipCheck = VerifySessionOwnership(sessionId, agentId);
            if (ownershipCheck is not null)
                return ownershipCheck;

            sb.Append(" AND parent_session_id = @sessionId");
            parameters.Add(new SqliteParameter("@sessionId", sessionId));
        }
        else
        {
            // Filter to sessions owned by the agent
            sb.Append(" AND parent_session_id IN (SELECT id FROM sessions WHERE conversation_id IN (SELECT id FROM conversations WHERE agent_id = @agentId))");
            parameters.Add(new SqliteParameter("@agentId", agentId));
        }

        sb.Append(" ORDER BY started_at DESC");
        sb.Append($" LIMIT {limit} OFFSET {offset}");

        return ExecuteReadQuery(sb.ToString(), parameters);
    }

    private AgentToolResult SessionInfo(IReadOnlyDictionary<string, object?> args)
    {
        var sessionId = GetString(args, "session_id");
        if (string.IsNullOrWhiteSpace(sessionId))
            return TextResult("Error: 'session_id' is required for session_info.");

        var agentId = GetString(args, "agent_id") ?? _agentId;
        var ownershipCheck = VerifySessionOwnership(sessionId, agentId);
        if (ownershipCheck is not null)
            return ownershipCheck;

        return ExecuteReadQuery(
            "SELECT * FROM sessions WHERE id = @sessionId",
            [new SqliteParameter("@sessionId", sessionId)]);
    }

    private AgentToolResult ConversationInfo(IReadOnlyDictionary<string, object?> args)
    {
        var conversationId = GetString(args, "conversation_id");
        if (string.IsNullOrWhiteSpace(conversationId))
            return TextResult("Error: 'conversation_id' is required for conversation_info.");

        var agentId = GetString(args, "agent_id") ?? _agentId;

        return ExecuteReadQuery(
            "SELECT * FROM conversations WHERE id = @conversationId AND agent_id = @agentId",
            [new SqliteParameter("@conversationId", conversationId), new SqliteParameter("@agentId", agentId)]);
    }

    private AgentToolResult RuntimeStatus()
    {
        if (_runtimeStateProvider is null)
            return TextResult("Error: Runtime state provider is not available.");

        return TextResult(_runtimeStateProvider.GetRuntimeStatus());
    }

    private AgentToolResult RawSql(IReadOnlyDictionary<string, object?> args)
    {
        if (!_config.AllowRawSql)
            return TextResult("Error: raw_sql is disabled. Set 'allowRawSql: true' in the debug tool configuration to enable.");

        var sql = GetString(args, "sql");
        if (string.IsNullOrWhiteSpace(sql))
            return TextResult("Error: 'sql' is required for raw_sql.");

        if (!StartsWithReadKeyword(sql))
            return TextResult("Error: Only SELECT statements are permitted. The query must start with SELECT (after optional whitespace/comments).");

        // Reject multiple statements. SQLite executes every statement in a batched command,
        // so a payload such as "SELECT 1; PRAGMA database_list" would pass the keyword check
        // yet still run the trailing statement — defeating the agent-scoping guards even under
        // the read-only connection.
        if (!IsSingleStatement(sql))
            return TextResult("Error: Only a single statement is permitted. " +
                "Multiple statements separated by ';' are not allowed.");

        var (_, limit) = GetPagination(args);

        return ExecuteReadQuery(sql, [], limit);
    }

    // ── Database helpers ──────────────────────────────────────────────────────

    private SqliteConnection OpenReadOnly()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private AgentToolResult ExecuteReadQuery(string sql, List<SqliteParameter> parameters, int? rowLimit = null)
    {
        try
        {
            using var connection = OpenReadOnly();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var param in parameters)
                command.Parameters.Add(param);

            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            var effectiveLimit = rowLimit ?? MaxLimit;
            var rowCount = 0;

            while (reader.Read() && rowCount < effectiveLimit)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var name = reader.GetName(i);
                    var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    row[name] = value;
                }
                rows.Add(row);
                rowCount++;
            }

            if (rows.Count == 0)
                return TextResult("No results found.");

            var json = JsonSerializer.Serialize(rows, JsonOptions);
            return TextResult(json);
        }
        catch (SqliteException ex)
        {
            return TextResult($"Error: SQLite error — {ex.Message}");
        }
    }

    private AgentToolResult? VerifySessionOwnership(string sessionId, string agentId)
    {
        try
        {
            using var connection = OpenReadOnly();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*) FROM sessions s
                INNER JOIN conversations c ON s.conversation_id = c.id
                WHERE s.id = @sessionId AND c.agent_id = @agentId";
            command.Parameters.Add(new SqliteParameter("@sessionId", sessionId));
            command.Parameters.Add(new SqliteParameter("@agentId", agentId));

            var count = Convert.ToInt64(command.ExecuteScalar());
            if (count == 0)
                return TextResult($"Error: Session '{sessionId}' not found or not owned by agent '{agentId}'.");

            return null;
        }
        catch (SqliteException ex)
        {
            return TextResult($"Error: SQLite error during ownership check — {ex.Message}");
        }
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    internal static string? GetString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            string s => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText(),
            _ => raw?.ToString()
        };
    }

    internal static int GetInt(IReadOnlyDictionary<string, object?> args, string key, int defaultValue)
    {
        if (!args.TryGetValue(key, out var raw)) return defaultValue;
        return raw switch
        {
            int i => i,
            long l => SaturateToInt32(l),
            string s when int.TryParse(s, out var parsed) => parsed,
            JsonElement je when je.ValueKind == JsonValueKind.Number => ReadNumber(je, defaultValue),
            JsonElement je when je.ValueKind == JsonValueKind.String && int.TryParse(je.GetString(), out var parsed) => parsed,
            _ => defaultValue
        };
    }

    /// <summary>
    /// Reads a JSON number into an <see cref="int"/> without throwing on out-of-Int32-range or
    /// fractional values. A direct <see cref="JsonElement.GetInt32"/> throws
    /// <see cref="FormatException"/>/<see cref="OverflowException"/> for those inputs, crashing the
    /// tool <em>before</em> the caller's <c>Math.Clamp</c> can bound the value. Saturating to the
    /// Int32 range (or falling back to <paramref name="defaultValue"/> for non-numeric tokens) lets
    /// the value flow into the clamp instead. Mirrors the fixes in WebSearchTool (#1339),
    /// ProcessTool (#1354) and CronTool (#1364).
    /// </summary>
    private static int ReadNumber(JsonElement je, int defaultValue)
    {
        if (je.TryGetInt32(out var i)) return i;
        if (je.TryGetInt64(out var l)) return SaturateToInt32(l);
        if (je.TryGetDouble(out var d)) return SaturateToInt32(d);
        return defaultValue;
    }

    private static int SaturateToInt32(long value) =>
        value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;

    private static int SaturateToInt32(double value)
    {
        if (double.IsNaN(value)) return 0;
        if (value >= int.MaxValue) return int.MaxValue;
        if (value <= int.MinValue) return int.MinValue;
        return (int)value;
    }

    internal static (int Offset, int Limit) GetPagination(IReadOnlyDictionary<string, object?> args)
    {
        var offset = Math.Max(0, GetInt(args, "offset", 0));
        var limit = Math.Clamp(GetInt(args, "limit", DefaultLimit), 1, MaxLimit);
        return (offset, limit);
    }

    /// <summary>
    /// Returns <see langword="true"/> only when <paramref name="sql"/> is a single read-only
    /// statement: it must begin with an allowed read keyword (SELECT/WITH/EXPLAIN/PRAGMA) and
    /// contain no statement separator beyond a single optional trailing semicolon.
    /// <para>
    /// A naive leading-keyword check is insufficient: SQLite executes every statement in a batch,
    /// so a payload such as <c>SELECT 1; DELETE FROM t</c> would pass a prefix check yet still run
    /// the trailing write. This combined gate guards both concerns.
    /// </para>
    /// </summary>
    internal static bool IsSelectOnly(string sql) =>
        StartsWithReadKeyword(sql) && IsSingleStatement(sql);

    /// <summary>
    /// Returns <see langword="true"/> when the first significant keyword (after leading whitespace
    /// and line/block comments) is one of the permitted read-only keywords.
    /// </summary>
    internal static bool StartsWithReadKeyword(string sql)
    {
        // Strip leading whitespace and line comments, then check first keyword
        var trimmed = sql.AsSpan().TrimStart();

        // Skip line comments (-- ...) and block comments (/* ... */)
        while (true)
        {
            if (trimmed.StartsWith("--"))
            {
                var newlineIdx = trimmed.IndexOf('\n');
                trimmed = newlineIdx >= 0 ? trimmed[(newlineIdx + 1)..].TrimStart() : [];
            }
            else if (trimmed.StartsWith("/*"))
            {
                var endIdx = trimmed.IndexOf("*/");
                trimmed = endIdx >= 0 ? trimmed[(endIdx + 2)..].TrimStart() : [];
            }
            else
            {
                break;
            }
        }

        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="sql"/> contains a single statement,
    /// allowing a single optional trailing semicolon. Semicolons inside single-quoted string
    /// literals are not treated as separators (SQLite escapes an embedded quote by doubling it,
    /// which this scan handles naturally because the closing quote toggles the literal state
    /// open again on the very next character).
    /// </summary>
    internal static bool IsSingleStatement(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return false;

        bool inLiteral = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char c = sql[i];
            if (c == '\'')
            {
                inLiteral = !inLiteral;
                continue;
            }
            if (inLiteral || c != ';') continue;

            // Found a statement separator outside a literal. Everything after it must be
            // whitespace for this to remain a single statement with a trailing semicolon.
            for (int j = i + 1; j < sql.Length; j++)
            {
                if (!char.IsWhiteSpace(sql[j])) return false;
            }
            return true;
        }

        // No semicolon outside a literal — single statement.
        return true;
    }

    private static AgentToolResult TextResult(string text) =>
        new([new AgentToolContent(AgentToolContentType.Text, text)]);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
}
