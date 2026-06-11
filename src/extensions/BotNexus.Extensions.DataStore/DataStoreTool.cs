using System.Text;
using System.Text.Json;
using BotNexus.Agent.Core.Tools;
using BotNexus.Agent.Core.Types;
using BotNexus.Agent.Providers.Core.Models;

namespace BotNexus.Extensions.DataStore;

/// <summary>
/// Agent-facing tool for managing a per-agent structured SQLite data store.
/// Supports JSON array ingestion with auto schema inference, SQL SELECT queries,
/// single-row inserts, row deletions, schema inspection, table listing, and table drops.
///
/// Only contributed when <see cref="DataStoreConfig.Enabled"/> is true.
/// </summary>
public sealed class DataStoreTool(IDataStoreBackend backend) : IAgentTool
{
    // Actions the tool supports.
    internal static readonly HashSet<string> ValidActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "ingest", "query", "insert", "update", "delete", "schema", "tables", "drop"
    };

    public string Name => "data_store";
    public string Label => "Data Store";

    public Tool Definition => new(
        Name,
        "Manage a per-agent structured SQLite data store. Ingest JSON arrays, run SELECT queries, insert rows, update rows, delete rows, inspect schema, list tables, or drop tables.",
        JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "action": {
                  "type": "string",
                  "enum": ["ingest", "query", "insert", "update", "delete", "schema", "tables", "drop"],
                  "description": "Action to perform: 'ingest' - bulk load a JSON array; 'query' - run a SELECT; 'insert' - add a single JSON object row; 'update' - modify rows matching a WHERE clause; 'delete' - remove rows matching a WHERE clause; 'schema' - show column names and types; 'tables' - list all tables; 'drop' - drop a table."
                },
                "table": {
                  "type": "string",
                  "description": "Table name (required for ingest, insert, update, delete, schema, drop). Lowercase alphanumeric + underscores only."
                },
                "data": {
                  "type": "string",
                  "description": "JSON array of objects for 'ingest', or single JSON object for 'insert'."
                },
                "set": {
                  "type": "string",
                  "description": "JSON object of column=value pairs for 'update' action (e.g. {\"status\":\"done\",\"count\":5})."
                },
                "sql": {
                  "type": "string",
                  "description": "SELECT statement for 'query' action. Only SELECT is permitted."
                },
                "where": {
                  "type": "string",
                  "description": "WHERE clause for 'update' and 'delete' actions (e.g. \"status = 'done'\"). Required to prevent accidental full-table operations."
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

    public async Task<AgentToolResult> ExecuteAsync(
        string toolCallId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default,
        AgentToolUpdateCallback? onUpdate = null)
    {
        var action = GetString(arguments, "action")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(action) || !ValidActions.Contains(action))
            return TextResult($"Error: Unknown action '{action}'. Valid actions: {string.Join(", ", ValidActions.Order())}.");

        DataStoreResult result = action switch
        {
            "ingest"  => await IngestAsync(arguments, cancellationToken),
            "query"   => await QueryAsync(arguments, cancellationToken),
            "insert"  => await InsertAsync(arguments, cancellationToken),
            "update"  => await UpdateAsync(arguments, cancellationToken),
            "delete"  => await DeleteAsync(arguments, cancellationToken),
            "schema"  => await SchemaAsync(arguments, cancellationToken),
            "tables"  => await backend.TablesAsync(cancellationToken),
            "drop"    => await DropAsync(arguments, cancellationToken),
            _         => DataStoreResult.Fail($"Unhandled action '{action}'.")
        };

        return result.Success
            ? TextResult(result.Payload ?? string.Empty)
            : TextResult($"Error: {result.Error}");
    }

    // ── Action helpers ────────────────────────────────────────────────────────

    private async Task<DataStoreResult> IngestAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var table = GetString(args, "table");
        var data  = GetString(args, "data");
        if (string.IsNullOrWhiteSpace(table)) return DataStoreResult.Fail("'table' is required for ingest.");
        if (!IsValidTableName(table)) return DataStoreResult.Fail($"Invalid table name '{table}'. Use lowercase letters, digits, and underscores only.");
        if (string.IsNullOrWhiteSpace(data))  return DataStoreResult.Fail("'data' (JSON array) is required for ingest.");
        return await backend.IngestAsync(table, data, ct);
    }

    private async Task<DataStoreResult> QueryAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var sql = GetString(args, "sql");
        if (string.IsNullOrWhiteSpace(sql)) return DataStoreResult.Fail("'sql' is required for query.");
        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            return DataStoreResult.Fail("Only SELECT statements are permitted in 'query'.");
        return await backend.QueryAsync(sql, ct);
    }

    private async Task<DataStoreResult> InsertAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var table = GetString(args, "table");
        var data  = GetString(args, "data");
        if (string.IsNullOrWhiteSpace(table)) return DataStoreResult.Fail("'table' is required for insert.");
        if (!IsValidTableName(table)) return DataStoreResult.Fail($"Invalid table name '{table}'. Use lowercase letters, digits, and underscores only.");
        if (string.IsNullOrWhiteSpace(data))  return DataStoreResult.Fail("'data' (JSON object) is required for insert.");
        return await backend.InsertAsync(table, data, ct);
    }

    private async Task<DataStoreResult> UpdateAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var table = GetString(args, "table");
        var set   = GetString(args, "set");
        var where = GetString(args, "where");
        if (string.IsNullOrWhiteSpace(table)) return DataStoreResult.Fail("'table' is required for update.");
        if (!IsValidTableName(table)) return DataStoreResult.Fail($"Invalid table name '{table}'. Use lowercase letters, digits, and underscores only.");
        if (string.IsNullOrWhiteSpace(set))   return DataStoreResult.Fail("'set' (JSON object of column=value pairs) is required for update.");
        if (string.IsNullOrWhiteSpace(where)) return DataStoreResult.Fail("'where' clause is required for update to prevent accidental full-table updates.");
        return await backend.UpdateAsync(table, set, where, ct);
    }

    private async Task<DataStoreResult> DeleteAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var table = GetString(args, "table");
        var where = GetString(args, "where");
        if (string.IsNullOrWhiteSpace(table)) return DataStoreResult.Fail("'table' is required for delete.");
        if (!IsValidTableName(table)) return DataStoreResult.Fail($"Invalid table name '{table}'. Use lowercase letters, digits, and underscores only.");
        if (string.IsNullOrWhiteSpace(where)) return DataStoreResult.Fail("'where' clause is required for delete to prevent accidental full-table wipe.");
        return await backend.DeleteAsync(table, where, ct);
    }

    private async Task<DataStoreResult> SchemaAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var table = GetString(args, "table");
        if (string.IsNullOrWhiteSpace(table)) return DataStoreResult.Fail("'table' is required for schema.");
        if (!IsValidTableName(table)) return DataStoreResult.Fail($"Invalid table name '{table}'.");
        return await backend.SchemaAsync(table, ct);
    }

    private async Task<DataStoreResult> DropAsync(IReadOnlyDictionary<string, object?> args, CancellationToken ct)
    {
        var table = GetString(args, "table");
        if (string.IsNullOrWhiteSpace(table)) return DataStoreResult.Fail("'table' is required for drop.");
        if (!IsValidTableName(table)) return DataStoreResult.Fail($"Invalid table name '{table}'.");
        return await backend.DropAsync(table, ct);
    }

    // ── Utilities ─────────────────────────────────────────────────────────────

    internal static string? GetString(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var raw)) return null;
        return raw switch
        {
            string s       => s,
            JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString(),
            _              => raw?.ToString()
        };
    }

    internal static bool IsValidTableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 63) return false;
        return name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
    }

    private static AgentToolResult TextResult(string text) =>
        new([new AgentToolContent(AgentToolContentType.Text, text)]);
}
