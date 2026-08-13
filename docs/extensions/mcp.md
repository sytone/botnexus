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

### Config is not manifest-validated

`botnexus-mcp`'s `botnexus-extension.json` declares `configSchema: []`, so none of the keys below are
validated or defaulted by the extension loader. This is a limitation of the manifest schema, not an
oversight in this extension: `ExtensionConfigValidator` only inspects **top-level** field presence —
it warns on an absent `required` field and applies a string `default` to an absent optional one. It
does not type-check values and does not descend into nested objects. MCP's configuration is a nested
`servers` map keyed by arbitrary server IDs, which that flat model cannot express.

Practical consequences:

- A typo in a server key, a missing `command`/`url`, or a wrong value type produces no startup warning.
- Defaults documented below are applied by the extension's own binding (`McpExtensionConfig`), not by
  the manifest.
- Validation failures surface at server start/connect time, per server, as warnings — a failing server
  is skipped and the others continue.

See [the manifest contract](/extension-development#configschema) for what `configSchema` can and
cannot express.

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
| `url` | string | — | URL for the MCP server endpoint. Must be `https` when the server carries credentials (see [Auth Injection](#auth-injection)); loopback `http` is exempt. |
| `headers` | object | — | Additional HTTP headers for requests. |
| `auth` | string | — | BotNexus provider key for automatic Bearer token injection. Requires an `https` (or loopback) `url`. |
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

### TLS requirement for credentialed servers

A resolved provider API key is a full BotNexus provider credential, not a scoped token, so it must
only ever leave the process over TLS. When a server carries credentials — either `auth` is set or
`headers` contains an `Authorization` entry — its `url` **must** use `https`. The one exception is
loopback (`http://localhost`, `http://127.0.0.1`, `http://[::1]`), which stays permitted as a
deliberate developer affordance for local MCP servers that have no certificate.

A credentialed server configured with a plaintext non-loopback `url` is **skipped**: it contributes
no tools, the credential is never resolved for it, and a warning naming the server id is logged:

```
MCP server 'remote-api' has auth=my-provider-key configured but its url is not usable for
credentials: url scheme 'http' would transmit credentials in cleartext to non-loopback host
'mcp.example.com'; https is required. Skipping server.
```

Servers with **no** credentials are unaffected — a plaintext `url` without `auth` or an
`Authorization` header continues to work as before.

BotNexus also disables HTTP auto-redirect on the HTTP/SSE transport's own client. Following a
redirect would replay the `Authorization` header to whatever host the server nominated, so a
redirect is surfaced to the caller instead of followed.

## Security Considerations

- **Credentialed servers require `https`**: an `auth` or `Authorization` server on a plaintext
  non-loopback `url` is refused rather than leaking a provider API key in cleartext. See
  [TLS requirement for credentialed servers](#tls-requirement-for-credentialed-servers).
- **No auto-redirect**: the transport does not follow HTTP redirects, so a configured bearer token
  cannot be replayed to a different host.
- **`inheritEnv: true` (default)**: The MCP subprocess inherits all parent environment variables, which may include secrets not intended for the server. Set to `false` for production servers.
- **Tool prefixing**: When multiple servers expose tools with the same name, prefixing prevents collisions and makes tool provenance clear.
- **Timeouts**: Configure `initTimeoutMs` and `callTimeoutMs` to prevent hung servers from blocking agent sessions.

## Warmup Cache

MCP servers are pre-started at gateway boot via `McpServerWarmupHostedService`. Tool schemas are cached so that agents don't wait for server initialization on their first tool call.

## Related

- [MCP Invoke](./mcp-invoke.md) — On-demand MCP server access without bridging
- [Extension Development](/extension-development) — Building custom extensions
