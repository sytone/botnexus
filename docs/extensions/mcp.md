# MCP (Model Context Protocol)

The MCP extension connects agents to external [Model Context Protocol](https://modelcontextprotocol.io/) servers, bridging their tools directly into the agent's tool palette. Each MCP server's tools appear as native agent tools.

## Overview

| Property | Value |
|----------|-------|
| Extension ID | `botnexus-mcp` |
| Tool names | Bridged from connected MCP servers (optionally prefixed with server ID) |
| Source | `BotNexus.Extensions.Mcp` |

## Capabilities

- Connect to MCP servers via stdio (subprocess) or HTTP/SSE transport
- Bridge MCP tools as native agent tools
- Optional tool name prefixing for disambiguation
- Server warmup and caching for faster session starts
- Per-server timeouts for initialization and tool calls
- Provider-based auth injection for HTTP/SSE servers

## Configuration

Configure MCP servers in your agent's extension config:

```json
{
  "extensions": {
    "botnexus-mcp": {
      "servers": {
        "filesystem": {
          "command": "npx",
          "args": ["-y", "@modelcontextprotocol/server-filesystem", "/home/user/projects"],
          "initTimeoutMs": 30000,
          "callTimeoutMs": 60000
        },
        "remote-api": {
          "url": "https://mcp.example.com/sse",
          "headers": {
            "X-Custom-Header": "value"
          },
          "auth": "my-provider-key"
        }
      },
      "toolPrefix": true
    }
  }
}
```

### Top-Level Settings

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `servers` | object | `{}` | MCP servers keyed by server ID. |
| `toolPrefix` | boolean | true | Prefix tool names with server ID (e.g., `filesystem__read_file`). |

### Server Configuration (stdio transport)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `command` | string | — | Command to spawn the MCP server process. |
| `args` | string[] | — | Arguments for the server command. |
| `env` | object | — | Environment variables for the server process. |
| `workingDirectory` | string | — | Working directory for the server process. |
| `inheritEnv` | boolean | true | Inherit parent process environment. Set `false` for production security. |
| `initTimeoutMs` | integer | 30000 | Timeout for server initialization in milliseconds. |
| `callTimeoutMs` | integer | 60000 | Timeout for tool calls in milliseconds. |

### Server Configuration (HTTP/SSE transport)

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `url` | string | — | URL for the MCP server endpoint. |
| `headers` | object | — | Additional HTTP headers for requests. |
| `auth` | string | — | BotNexus provider key for automatic Bearer token injection. |
| `initTimeoutMs` | integer | 30000 | Timeout for server initialization. |
| `callTimeoutMs` | integer | 60000 | Timeout for tool calls. |

## Transport Types

### Stdio

The server runs as a subprocess. BotNexus spawns the process using `command` and `args`, communicating via stdin/stdout using the MCP JSON-RPC protocol.

Best for: local tools, filesystem access, development servers.

### HTTP/SSE

The server runs externally and BotNexus connects via HTTP with Server-Sent Events for streaming.

Best for: remote services, shared infrastructure, cloud-hosted MCP servers.

## Auth Injection

For HTTP/SSE servers, set the `auth` field to a BotNexus provider key. At session start, BotNexus resolves a Bearer token via `GetProviderApiKeyAsync` and injects it as an `Authorization: Bearer <token>` header.

An explicit `Authorization` header in the `headers` config takes precedence over `auth`.

## Security Considerations

- **`inheritEnv: true` (default)**: The MCP subprocess inherits all parent environment variables, which may include secrets not intended for the server. Set to `false` for production servers.
- **Tool prefixing**: When multiple servers expose tools with the same name, prefixing prevents collisions and makes tool provenance clear.
- **Timeouts**: Configure `initTimeoutMs` and `callTimeoutMs` to prevent hung servers from blocking agent sessions.

## Warmup Cache

MCP servers are pre-started at gateway boot via `McpServerWarmupHostedService`. Tool schemas are cached so that agents don't wait for server initialization on their first tool call.

## Related

- [MCP Invoke](./mcp-invoke.md) — On-demand MCP server access without bridging
- [Extension Development](/extension-development) — Building custom extensions
