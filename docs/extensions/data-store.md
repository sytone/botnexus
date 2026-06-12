# Data Store

The Data Store extension provides agents with a per-agent structured SQLite database for persistent data storage. Agents can ingest JSON arrays, run SQL queries, and manage tables — all through a single tool.

## Overview

| Property | Value |
|----------|-------|
| Extension ID | `botnexus-data-store` |
| Tool name | `data_store` |
| Source | `BotNexus.Extensions.DataStore` |

## Capabilities

- Bulk ingest JSON arrays with automatic schema inference
- Run SELECT queries against stored data
- Count rows in a table with optional filtering
- Insert individual rows
- Update existing rows matching conditions
- Delete rows matching conditions
- Inspect table schemas
- List all tables
- Drop tables

## Tool Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | Action: `ingest`, `query`, `count`, `insert`, `update`, `delete`, `schema`, `tables`, or `drop`. |
| `table` | string | Conditional | Table name. Required for `ingest`, `insert`, `delete`, `schema`, `drop`. Lowercase alphanumeric + underscores only. |
| `data` | string | Conditional | JSON array of objects for `ingest`, or single JSON object for `insert`. |
| `sql` | string | Conditional | SELECT statement for `query` action. Only SELECT is permitted. |
| `set` | string | Conditional | JSON object of column=value pairs for `update` action (e.g. `{"status": "done"}`). |
| `where` | string | Conditional | WHERE clause for `update` and `delete` actions (required to prevent accidental full-table wipe). |

## Configuration

Enable the data store in your agent's extension config:

```json
{
  "extensions": {
    "botnexus-data-store": {
      "enabled": true,
      "maxSizeBytes": 52428800
    }
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `enabled` | boolean | false | Whether the data store tool is available to this agent. |
| `maxSizeBytes` | integer | 52428800 (50 MB) | Maximum size for the per-agent SQLite database. |

## Actions

### `ingest`

Bulk load a JSON array into a table. If the table doesn't exist, it is created with columns inferred from the first object's keys and value types.

```json
{
  "action": "ingest",
  "table": "contacts",
  "data": "[{\"name\": \"Alice\", \"email\": \"alice@example.com\"}, {\"name\": \"Bob\", \"email\": \"bob@example.com\"}]"
}
```

### `query`

Run a SELECT statement and return results as JSON.

```json
{
  "action": "query",
  "sql": "SELECT name, email FROM contacts WHERE name LIKE 'A%'"
}
```

### `count`

Count rows in a table, optionally filtered by a WHERE clause.

```json
{
  "action": "count",
  "table": "contacts",
  "where": "name LIKE 'A%'"
}
```

Returns: `{"table": "contacts", "count": 42}`

The `where` parameter is optional — omit it to count all rows.

### `insert`

Add a single row to a table.

```json
{
  "action": "insert",
  "table": "contacts",
  "data": "{\"name\": \"Charlie\", \"email\": \"charlie@example.com\"}"
}
```

### `update`

Modify existing rows matching a WHERE clause. Both `set` and `where` are required.

```json
{
  "action": "update",
  "table": "contacts",
  "set": "{\"email\": \"alice@newdomain.com\"}",
  "where": "name = 'Alice'"
}
```

The `set` parameter is a JSON object where keys are column names and values are the new values. The `where` clause prevents accidental full-table updates.

### `delete`

Remove rows matching a WHERE clause. The `where` parameter is required to prevent accidental full-table deletion.

```json
{
  "action": "delete",
  "table": "contacts",
  "where": "name = 'Bob'"
}
```

### `schema`

Show column names and types for a table.

```json
{
  "action": "schema",
  "table": "contacts"
}
```

### `tables`

List all tables in the agent's data store.

```json
{
  "action": "tables"
}
```

### `drop`

Drop (permanently delete) a table.

```json
{
  "action": "drop",
  "table": "old_data"
}
```

## Behavior Notes

- Each agent has its own isolated SQLite database file.
- Only SELECT queries are permitted via the `query` action — INSERT, UPDATE, DELETE, and DDL are blocked.
- Schema is inferred from JSON types: strings → TEXT, numbers → REAL, booleans → INTEGER.
- The database size is enforced at the configured limit; ingestion fails if the limit would be exceeded.
- Table names must be lowercase alphanumeric with underscores only.

## Related

- [Configuration Reference](/configuration) — Full platform configuration reference
