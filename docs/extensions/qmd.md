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
| `id` | string | Yes | Document ID or path returned by search. For a memory result, use the full `memory:<store>/<entryId>` ID. |

`knowledge_get` has no separate `store` argument. It checks the returned document's
store against `allowedStores` before returning its content.

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
      "includeMemoryStores": false,
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
| `allowedStores` | string[] | *(all)* | Store-name allowlist used by the tools, matched case-insensitively. Null or empty means unrestricted by this list. See the search limitation below. |
| `includeMemoryStores` | boolean | false | Add readable shared memory stores alongside the CLI backend when a shared-memory registry is available. |

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

## Shared Memory Collections

With `includeMemoryStores: true` and an available shared-memory registry, the
contributor combines the QMD CLI backend with `MemoryQmdBackend`. Without the
registry, it keeps the CLI backend alone. This setting does not turn off the CLI
backend or remove its prerequisites.

A shared store named `team-notes` appears as the virtual collection
`memory:team-notes`. Search results use IDs such as
`memory:team-notes/<entryId>` and paths such as `memory://team-notes/<entryId>`.
Use the complete ID from a result with `knowledge_get`. If `allowedStores` is
nonempty, include the prefixed collection name for explicit searches and reads.

The memory backend lists only stores the registry allows this agent to read.
Explicit search targets and document reads also check the registry's read policy;
setting `includeMemoryStores` does not grant access to other agents' stores. Memory
indexing and embedding remain owned by the memory pipeline, not the QMD CLI.
The memory backend uses its store's text search for all QMD mode values and supplies
a fixed score of `0.8`; selecting `semantic` does not make that backend perform
semantic search.

The composite backend calls the CLI first. A CLI failure can prevent it from
reaching the memory backend; this is not a memory-only fallback mode.

## Per-Agent Store Scoping

The tools use `allowedStores` as follows:

- `knowledge_search` checks an explicitly supplied `store` against the allowlist.
- `knowledge_stores` filters the returned collection list.
- `knowledge_get` checks the retrieved document's store before returning content.

::: warning Unscoped searches are not filtered by allowedStores
The current `knowledge_search` implementation does not apply the allowlist when
`store` is omitted, and does not filter the returned results by store. Do not rely
on `allowedStores` alone as a multi-tenant search boundary. The memory backend's
separate registry read checks still apply.
:::

Sources: [QmdToolContributor](https://github.com/Sytone/botnexus/blob/main/src/extensions/BotNexus.Extensions.Qmd/QmdToolContributor.cs),
[MemoryQmdBackend](https://github.com/Sytone/botnexus/blob/main/src/extensions/BotNexus.Extensions.Qmd/MemoryQmdBackend.cs),
and [KnowledgeSearchTool](https://github.com/Sytone/botnexus/blob/main/src/extensions/BotNexus.Extensions.Qmd/KnowledgeSearchTool.cs).

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
