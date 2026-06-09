# MCP Invoke

The MCP Invoke extension provides a single `invoke_mcp` tool for calling MCP servers on demand, without bridging their tools into the agent's main tool palette. Servers start lazily on first use and stay alive for the session.

## Overview

| Property | Value |
|----------|-------|
| Extension ID | `botnexus-mcp-invoke` |
| Tool name | `invoke_mcp` |
| Source | `BotNexus.Extensions.McpInvoke` |

## When to Use

Use **MCP** (bridging) when:
- You want MCP tools to appear directly in the agent's tool list
- The agent should see and choose MCP tools naturally

Use **MCP Invoke** when:
- You have many MCP servers and don't want to pollute the tool list
- Skills describe which servers and tools to call
- You want on-demand server startup (no warmup cost)

## Tool Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `server` | string | Yes | Server ID as configured in the extension config. |
| `tool` | string | Yes | Tool name to invoke on the server. |
| `arguments` | object | No | Arguments to pass to the MCP tool. |

## Configuration

```json
{
  "extensions": {
    "botnexus-mcp-invoke": {
      "enabled": true,
      "servers": {
        "github": {
          "command": "npx",
          "args": ["-y", "@modelcontextprotocol/server-github"],
          "env": {
            "GITHUB_TOKEN": "${env:GITHUB_TOKEN}"
          }
        },
        "postgres": {
          "command": "npx",
          "args": ["-y", "@modelcontextprotocol/server-postgres"],
          "env": {
            "DATABASE_URL": "${env:DATABASE_URL}"
          }
        }
      }
    }
  }
}
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `enabled` | boolean | true | Whether the invoke_mcp tool is available. |
| `servers` | object | `{}` | MCP servers accessible via invoke_mcp, keyed by server ID. |

Server configuration follows the same schema as the [MCP extension](./mcp.md) (supports both stdio and HTTP/SSE transport).

## Usage Example

```json
{
  "server": "github",
  "tool": "create_issue",
  "arguments": {
    "owner": "myorg",
    "repo": "myrepo",
    "title": "Bug: login page broken",
    "body": "Steps to reproduce..."
  }
}
```

## Behavior Notes

- Servers start lazily on first `invoke_mcp` call and remain alive for the session duration.
- The agent never sees individual MCP tool schemas — it relies on skills or instructions to know which servers and tools are available.
- If a server fails to start or a tool call times out, an error is returned to the agent.
- Server configuration reuses the same `McpServerConfig` schema as the MCP bridging extension.

## Related

- [MCP](./mcp.md) — Bridge MCP tools directly into the agent's tool palette
- [Skills](/skills) — Define skill documents that guide agents on which MCP tools to invoke
