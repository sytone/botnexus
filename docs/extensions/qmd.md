# QMD (Knowledge Base)

The QMD extension integrates a local knowledge base into BotNexus agents, providing keyword, semantic, and hybrid search over document collections. It wraps the external `qmd` CLI binary.

> **QMD is optional and disabled by default.** No knowledge tools are contributed and no indexing
> process starts unless an agent explicitly sets `extensions.botnexus-qmd.enabled: true`. Missing,
> empty, or malformed configuration fails closed to disabled (malformed config logs a diagnostic).
>
> **Changed default (issue #2116):** earlier builds treated an omitted `botnexus-qmd` block as
> *enabled*. That is no longer the case. Installs that relied on omission to mean "enabled" must now
> add an explicit `"enabled": true` to keep QMD active.

## Overview

| Property | Value |
|----------|-------|
| Extension ID | `botnexus-qmd` |
| Tool names | `knowledge_search`, `knowledge_stores`, `knowledge_get` |
| Source | `BotNexus.Extensions.Qmd` |

## Tools

### `knowledge_search`

Search the knowledge base using keyword, semantic, or hybrid search.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `query` | string | Yes | Search query (natural language or keywords). |
| `store` | string | No | Target store name. Omit to search all allowed stores. |
| `mode` | string | No | Search mode: `keyword`, `semantic`, or `hybrid`. Default from config. |
| `limit` | integer | No | Maximum results to return (1–50). Default from config. |

### `knowledge_stores`

List available knowledge stores with their descriptions.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| *(none)* | — | — | Lists all stores the agent is allowed to access. |

### `knowledge_get`

Retrieve a specific document by ID from a store.

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `id` | string | Yes | Document identifier. |
| `store` | string | Yes | Store name containing the document. |

## Configuration

Configure in your agent's extension config block:

```json
{
  "extensions": {
    "botnexus-qmd": {
      "enabled": true,
      "qmdPath": null,
      "defaultSearchMode": "hybrid",
      "maxResults": 10,
      "stores": [
        {
          "name": "docs",
          "path": "/path/to/documents",
          "description": "Project documentation",
          "autoUpdate": true,
          "updateIntervalMinutes": 60
        }
      ],
      "allowedStores": ["docs"]
    }
  }
}
```

### Extension Settings

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `enabled` | boolean | **false** | Whether the QMD extension is enabled for this agent. Must be explicitly set to `true` to activate QMD; omitted/empty/malformed config is treated as disabled. |
| `qmdPath` | string | null | Path to the `qmd` binary. When null, resolved from PATH. |
| `defaultSearchMode` | string | `"hybrid"` | Default search mode: `keyword`, `semantic`, or `hybrid`. |
| `maxResults` | integer | 10 | Default maximum number of search results. |
| `stores` | array | `[]` | Knowledge stores to index and search. |
| `allowedStores` | string[] | *(all)* | Store names this agent can access. Omit to allow all configured stores. |

### Store Configuration

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `name` | string | *(required)* | Unique name for the store (used in search queries and output). |
| `path` | string | *(required)* | Filesystem path to the document folder. |
| `description` | string | null | Human-readable description of the store's contents. |
| `autoUpdate` | boolean | true | Whether to automatically re-index on a schedule. |
| `updateIntervalMinutes` | integer | 60 | Interval between automatic re-indexing runs. |

## Auto-Indexing

When `autoUpdate` is enabled on a store, BotNexus runs a background service (`QmdIndexHostedService`) that periodically re-indexes each store. Health tracking reports consecutive failures and marks stores as unhealthy after repeated errors. The index process has a 5-minute timeout per store.

## Per-Agent Store Scoping

Use `allowedStores` to restrict which stores an agent can search. When set:

- `knowledge_search` rejects queries to non-allowed stores
- `knowledge_stores` only lists allowed stores
- `knowledge_get` validates the document's store against the allowlist

This enables multi-tenant deployments where different agents have access to different knowledge bases.

## Prerequisites

- The `qmd` CLI binary must be installed and available on PATH (or provide `qmdPath`)
- Document folders must be accessible from the gateway process

## Behavior Notes

- Documents are truncated to 50K characters in `knowledge_get` responses
- The `qmd` CLI is invoked with `--json` flag for structured output
- A 30-second timeout applies to each CLI invocation
- If the `qmd` binary is not found, tools return an informative error

## Related

- [Skills Extension](/extensions/skills) — Script-based agent capabilities
- [Web Tools](/extensions/web-tools) — Web search and URL fetching
