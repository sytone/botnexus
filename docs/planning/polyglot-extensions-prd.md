# PRD: Polyglot Extension System

**Author:** Jon Bullen
**Date:** 2026-04-08
**Status:** Draft
**Area:** Extension System / Platform Architecture

---

## Executive Summary

BotNexus extensions today are C#/.NET assemblies loaded at runtime via `AssemblyLoadContext`. This works well for .NET developers but excludes a large population of potential extension authors who write in TypeScript/JavaScript or Python — the two most common languages in the AI and automation ecosystem.

This PRD defines a polyglot extension model that lets extension authors write **tools**, **channels**, and **providers** in C#, TypeScript/JavaScript, or Python while maintaining the safety, discoverability, and lifecycle management of the existing system.

---

## Problem Statement

**Who is affected:** Extension authors and the BotNexus ecosystem.

**The problem:** The current extension system only supports C#/.NET. This limits the addressable community of extension authors and prevents reuse of the rich Python (LangChain, transformers, pandas) and TypeScript/JavaScript (npm) ecosystems. Many AI-adjacent tools, SDKs, and libraries are published Python-first or JS-first. Extension authors who are proficient in these languages must either learn C# or build workarounds (shell tools, HTTP wrappers) that bypass the extension lifecycle.

**Why now:** The AI tooling ecosystem is maturing rapidly around Python and TypeScript. BotNexus's value as a platform scales with the number and quality of available extensions. Polyglot support removes the biggest friction point for community growth.

---

## Goals

| # | Goal | Success Metric |
|---|------|---------------|
| G1 | Extension authors can write tools in Python or TypeScript using their native ecosystems (pip, npm) | A Python tool extension using a pip package runs successfully in BotNexus |
| G2 | Polyglot extensions have the same lifecycle (discover, load, register, health-check, stop) as C# extensions | Polyglot extensions appear in the extension registry and respond to start/stop signals |
| G3 | The platform team maintains a single protocol, not per-language integration code | One JSON-RPC protocol spec; thin SDKs per language |
| G4 | C# extensions remain first-class and are not degraded | Existing C# extensions work without changes |
| G5 | Polyglot extensions are process-isolated for crash safety | A crashing Python extension does not take down the host |

## Non-Goals

- Hot-reload of polyglot extensions at runtime (future)
- In-process embedding (CSnakes, ClearScript) — see [Future Considerations](#future-considerations)
- A marketplace or distribution system for extensions
- Supporting languages beyond C#, TypeScript/JavaScript, and Python
- WebAssembly/WASM sandboxing (future)

---

## Target Audience

| Persona | Description | Primary Language |
|---------|-------------|-----------------|
| **Platform developer** | Builds and maintains BotNexus core | C# |
| **Extension author (internal)** | Builds tools/channels for internal use | C#, Python, or TypeScript |
| **Extension author (community)** | Builds tools/channels for the ecosystem | Python or TypeScript (most likely) |

---

## Proposed Solution

### Architecture: Process + JSON-RPC over stdio

Each polyglot extension runs as a **child process** managed by the BotNexus host. Communication uses **JSON-RPC 2.0 over stdin/stdout** — the same protocol pattern proven by VS Code extensions (50K+), MCP tool servers, and LSP language servers.

```
┌─────────────────────────────────────────────────────────┐
│  BotNexus Host (C#)                                     │
│                                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │            Extension Manager                      │  │
│  │  ┌─────────┐  ┌──────────┐  ┌─────────────────┐  │  │
│  │  │ C# Exts │  │ Process  │  │ Extension       │  │  │
│  │  │ (in-    │  │ Host     │  │ Registry        │  │  │
│  │  │ process)│  │ Manager  │  │ (unified view)  │  │  │
│  │  └─────────┘  └────┬─────┘  └─────────────────┘  │  │
│  │                    │                               │  │
│  └────────────────────┼───────────────────────────────┘  │
│                       │                                  │
└───────────────────────┼──────────────────────────────────┘
                        │ stdio (JSON-RPC 2.0)
          ┌─────────────┼─────────────┐
          │             │             │
     ┌────▼───┐   ┌────▼───┐   ┌────▼───┐
     │ python │   │ node   │   │ dotnet │
     │ ext.py │   │ ext.ts │   │ ext.dll│
     │ (pip)  │   │ (npm)  │   │ (rare) │
     └────────┘   └────────┘   └────────┘
```

### Why JSON-RPC over stdio?

| Alternative | Reason for Rejection |
|-------------|---------------------|
| gRPC | Requires protobuf compilation, heavier SDK, overkill for tool calls |
| HTTP/REST | Requires port management, firewall considerations, no built-in lifecycle |
| WebSocket | More complex than needed for request/response tool patterns |
| In-process (CSnakes, V8) | No isolation, crash propagation, complex deployment — suitable as future performance optimization only |
| WASM (Extism) | Limited package ecosystem access (no pip/npm from within WASM sandbox) |

**JSON-RPC over stdio advantages:**
- Zero network configuration (no ports, no firewalls)
- Built-in lifecycle management (parent kills child on shutdown)
- Full access to native package ecosystems (pip, npm)
- Process isolation — crash safety by default
- Proven at scale (VS Code, MCP, LSP)
- Simple SDK: ~200-500 lines per language
- Debuggable — attach to the child process

---

## Detailed Design

### 1. Extension Manifest (Extended)

The existing `botnexus-extension.json` manifest gains new fields for polyglot extensions:

```jsonc
{
  "id": "weather-tool",
  "name": "Weather Tool",
  "version": "1.0.0",
  "description": "Get weather forecasts using OpenWeatherMap API",

  // --- Existing fields (C# extensions) ---
  "entryAssembly": "BotNexus.Tools.Weather.dll",
  "extensionTypes": ["tool"],
  "dependencies": [],

  // --- New fields (polyglot extensions) ---
  "runtime": "python",                    // "dotnet" (default) | "python" | "node"
  "entryPoint": "main.py",               // Script to execute (replaces entryAssembly for non-dotnet)
  "runtimeVersion": ">=3.10",            // Optional: minimum runtime version
  "installCommand": "pip install -r requirements.txt",  // Optional: dependency install command

  // Tool definitions for process-based extensions (replaces IAgentTool discovery)
  "tools": [
    {
      "name": "get_weather",
      "description": "Get current weather for a city",
      "parameters": {
        "type": "object",
        "properties": {
          "city": { "type": "string", "description": "City name" },
          "units": { "type": "string", "enum": ["metric", "imperial"], "default": "metric" }
        },
        "required": ["city"]
      }
    }
  ]
}
```

**Key decisions:**
- `runtime: "dotnet"` is the default — existing C# extensions work unchanged
- For `python` and `node`, `entryPoint` replaces `entryAssembly`
- Tool schemas are declared in the manifest (the host doesn't need to call the extension to discover tools)
- The `installCommand` enables automatic dependency installation on first load

### 2. JSON-RPC Protocol

The host communicates with polyglot extension processes via JSON-RPC 2.0 over stdin/stdout. Stderr is captured for logging/diagnostics.

#### Lifecycle Methods

```
Host                          Extension Process
  │                                  │
  │──── initialize ─────────────────▶│   Handshake: protocol version, capabilities
  │◀─── result ─────────────────────│
  │                                  │
  │──── tools/call ─────────────────▶│   Execute a tool
  │◀─── result ─────────────────────│
  │                                  │
  │──── shutdown ───────────────────▶│   Graceful shutdown
  │◀─── result ─────────────────────│
  │                                  │
```

#### Method: `initialize`

```jsonc
// Request
{
  "jsonrpc": "2.0",
  "id": 1,
  "method": "initialize",
  "params": {
    "protocolVersion": "1.0",
    "hostInfo": { "name": "botnexus", "version": "1.0.0" },
    "workingDirectory": "/path/to/workspace",
    "configuration": { /* extension-specific config from botnexus-config.json */ }
  }
}

// Response
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "protocolVersion": "1.0",
    "extensionInfo": { "name": "weather-tool", "version": "1.0.0" },
    "capabilities": {
      "tools": true,
      "streaming": false,       // Future: streaming tool output
      "healthCheck": true       // Extension supports health/ping
    }
  }
}
```

#### Method: `tools/call`

```jsonc
// Request
{
  "jsonrpc": "2.0",
  "id": 2,
  "method": "tools/call",
  "params": {
    "name": "get_weather",
    "arguments": { "city": "Seattle", "units": "metric" },
    "toolCallId": "call_abc123"
  }
}

// Response (success)
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "content": [
      { "type": "text", "text": "{\"temperature\": 18, \"condition\": \"cloudy\"}" }
    ],
    "isError": false
  }
}

// Response (error)
{
  "jsonrpc": "2.0",
  "id": 2,
  "result": {
    "content": [
      { "type": "text", "text": "API rate limit exceeded. Try again in 60 seconds." }
    ],
    "isError": true
  }
}
```

#### Method: `shutdown`

```jsonc
// Request
{ "jsonrpc": "2.0", "id": 99, "method": "shutdown" }

// Response
{ "jsonrpc": "2.0", "id": 99, "result": {} }
```

#### Method: `health` (Optional)

```jsonc
// Request
{ "jsonrpc": "2.0", "id": 3, "method": "health" }

// Response
{ "jsonrpc": "2.0", "id": 3, "result": { "status": "ok" } }
```

### 3. Host-Side Components

#### ProcessExtensionHost

A new class that manages the lifecycle of a polyglot extension process:

```
ProcessExtensionHost
├── Spawns the runtime process (python, node, dotnet)
├── Manages JSON-RPC communication over stdio
├── Implements IAgentTool for each tool declared in the manifest
├── Handles process crashes with restart policy
├── Captures stderr for logging
└── Implements graceful shutdown
```

**Responsibilities:**
- Start the extension process with the correct runtime command
- Send `initialize` on startup
- Create `ProcessAgentTool` wrapper instances for each tool in the manifest
- Route `tools/call` requests to the process and parse responses
- Monitor process health (heartbeat or on-demand ping)
- Restart on crash (configurable: none, once, always)
- Send `shutdown` on host stop, force-kill after timeout

#### ProcessAgentTool

A thin `IAgentTool` implementation that wraps a tool declared in a polyglot extension manifest. From the agent's perspective, it looks identical to a C# tool.

```csharp
public sealed class ProcessAgentTool : IAgentTool
{
    // Name, Label, Definition come from the manifest
    // ExecuteAsync sends JSON-RPC tools/call to the ProcessExtensionHost
    // PrepareArgumentsAsync validates against the JSON Schema from the manifest
}
```

#### Updated Extension Loader

The `AssemblyLoadContextExtensionLoader` is extended to:
1. Check the `runtime` field in `botnexus-extension.json`
2. If `runtime` is `dotnet` (or absent) → existing assembly loading path
3. If `runtime` is `python` or `node` → create a `ProcessExtensionHost`
4. Register the `ProcessAgentTool` instances in the DI container

### 4. Extension Author SDKs

Thin SDK libraries that handle JSON-RPC protocol mechanics, letting extension authors focus on tool logic.

#### Python SDK (`botnexus-sdk`)

```python
# pip install botnexus-sdk

from botnexus import Extension, tool

class WeatherExtension(Extension):
    @tool(
        name="get_weather",
        description="Get current weather for a city",
    )
    async def get_weather(self, city: str, units: str = "metric") -> str:
        # Use any pip package
        import httpx
        resp = await httpx.AsyncClient().get(
            f"https://api.openweathermap.org/data/2.5/weather?q={city}&units={units}"
        )
        return resp.text

if __name__ == "__main__":
    WeatherExtension().run()  # Starts JSON-RPC stdio loop
```

**SDK responsibilities:**
- JSON-RPC 2.0 stdin/stdout protocol handling
- `@tool` decorator for tool registration with type-hint-based schema generation
- Async support (asyncio)
- Structured error handling
- Logging to stderr (separate from JSON-RPC on stdout)

#### TypeScript/JavaScript SDK (`@botnexus/sdk`)

```typescript
// npm install @botnexus/sdk

import { Extension, tool } from "@botnexus/sdk";

class WeatherExtension extends Extension {
  @tool({
    name: "get_weather",
    description: "Get current weather for a city",
  })
  async getWeather(args: { city: string; units?: string }): Promise<string> {
    // Use any npm package
    const resp = await fetch(
      `https://api.openweathermap.org/data/2.5/weather?q=${args.city}&units=${args.units ?? "metric"}`
    );
    return await resp.text();
  }
}

new WeatherExtension().run(); // Starts JSON-RPC stdio loop
```

**SDK responsibilities:**
- JSON-RPC 2.0 stdin/stdout protocol handling
- `@tool` decorator for tool registration
- TypeScript type support (parameters inferred from types or explicit schema)
- Promise/async-await support
- Logging to stderr

### 5. Extension Directory Structure (Polyglot)

```
extensions/
├── tools/
│   ├── github/                         # C# extension (existing)
│   │   ├── botnexus-extension.json
│   │   ├── BotNexus.Tools.GitHub.dll
│   │   └── ...
│   ├── weather/                        # Python extension (new)
│   │   ├── botnexus-extension.json
│   │   ├── main.py
│   │   ├── requirements.txt
│   │   └── weather_client.py
│   └── translator/                     # TypeScript extension (new)
│       ├── botnexus-extension.json
│       ├── package.json
│       ├── dist/
│       │   └── index.js                # Compiled TS
│       └── src/
│           └── index.ts
```

### 6. Runtime Resolution

The host needs to find the correct runtime binary:

| Runtime | Discovery Order |
|---------|----------------|
| `python` | 1. `BOTNEXUS_PYTHON` env var → 2. `python3` on PATH → 3. `python` on PATH |
| `node` | 1. `BOTNEXUS_NODE` env var → 2. `node` on PATH |
| `dotnet` | Existing assembly loading (no process spawn) |

For TypeScript extensions, the host runs `node dist/index.js` (pre-compiled). The `installCommand` in the manifest can handle `npm install && npm run build`.

### 7. Configuration

Extension-specific configuration is passed via the `initialize` method from the host config:

```jsonc
// ~/.botnexus/config.json
{
  "extensions": {
    "enabled": true,
    "path": "./extensions",
    "weather": {
      "apiKey": "sk-...",
      "defaultUnits": "metric"
    }
  }
}
```

The `extensions.weather` block is forwarded to the weather extension in the `initialize` request as `configuration`.

---

## Scope

### In Scope

- ProcessExtensionHost — manages polyglot extension processes
- ProcessAgentTool — IAgentTool wrapper for process-based tools
- Updated extension loader to handle `runtime` field in manifests
- Python SDK (`botnexus-sdk` pip package)
- TypeScript/JavaScript SDK (`@botnexus/sdk` npm package)
- Extended `botnexus-extension.json` manifest schema
- Extension scaffolding CLI command (`botnexus new tool --runtime python`)
- Documentation and getting-started guides
- Process health monitoring and restart policy

### Out of Scope

- Channel and provider extensions in Python/TS (tools only in v1 — see [Phasing](#phasing))
- In-process embedding for performance (CSnakes, ClearScript)
- Extension marketplace or distribution
- WASM sandboxing
- Hot-reload of running extensions
- Extension-to-extension communication

---

## Phasing

### Phase 1: Tool Extensions (MVP)

Enable Python and TypeScript **tool** extensions only. Tools are the simplest contract (request → response) and the highest-value extension type for the community.

**Deliverables:**
- `ProcessExtensionHost` with JSON-RPC stdio communication
- `ProcessAgentTool` implementing `IAgentTool`
- Extended manifest schema with `runtime`, `entryPoint`, `tools`
- Python SDK with `@tool` decorator and stdio loop
- TypeScript SDK with `@tool` decorator and stdio loop
- Updated extension loader
- One example extension per language
- Documentation update to extension-development.md

### Phase 2: Channels & Providers

Extend the JSON-RPC protocol to support:
- Channel adapter methods (`start`, `stop`, `send`, `onMessage` notifications)
- Provider methods (`stream`, `onEvent` notifications)

These are more complex because they involve bidirectional streaming and long-lived connections. Phase 2 should be informed by learnings from Phase 1.

### Phase 3: Developer Experience

- `botnexus new tool --runtime python` scaffolding
- Extension debugging guide (attach to child process)
- Extension testing framework (mock host)
- Performance profiling and optimization
- Extension dependency resolution across languages

---

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| JSON-RPC latency for high-frequency tool calls | Slower tool execution vs in-process | Acceptable for most tools (1-50ms overhead); offer in-process path later for hot-path tools |
| Python/Node not installed on host machine | Extension fails to load | Clear error messages; runtime version check at load time; document prerequisites |
| Extension process crashes | Tool becomes unavailable | Configurable restart policy (none, once, exponential backoff); health monitoring |
| Stdin/stdout conflicts (extension prints to stdout) | JSON-RPC protocol breaks | SDKs redirect all user output to stderr; validate protocol framing |
| Manifest tool schema out of sync with implementation | Runtime errors | SDK auto-generates schema from type hints/decorators; validate at `initialize` time |
| Large responses over stdio | Memory/performance issues | Configurable max response size; streaming support in Phase 2 |

---

## Dependencies

| Dependency | Type | Notes |
|-----------|------|-------|
| Python 3.10+ | Runtime | Required only if Python extensions are used |
| Node.js 18+ | Runtime | Required only if Node extensions are used |
| `System.Text.Json` | NuGet (existing) | JSON-RPC serialization |
| `System.IO.Pipelines` | NuGet (existing) | Efficient stdio stream handling |

No new NuGet dependencies required for the host-side implementation.

---

## Success Metrics

| Metric | Target |
|--------|--------|
| A Python tool extension using a pip package (e.g., `httpx`) executes successfully | Pass |
| A TypeScript tool extension using an npm package executes successfully | Pass |
| Existing C# extensions work without any changes | Pass |
| Extension process crash does not affect the host | Pass |
| Tool call latency overhead vs direct function call | < 50ms |
| Extension author can go from `botnexus new tool --runtime python` to working tool | < 15 minutes |
| SDK package size | < 50KB per language |

---

## Open Questions

- [ ] Should the SDKs be published to public registries (PyPI, npm) or distributed as vendored files?
- [ ] Should we support a "dev mode" where the host watches for file changes and auto-restarts the extension process?
- [ ] Should tool schemas be declared in the manifest, discovered via `initialize`, or both?
- [ ] What's the restart policy default — `once` or `none`?
- [ ] Should extensions have access to the BotNexus config store, or only their own section?

---

## Future Considerations

### In-Process Performance Path

For extensions that need sub-millisecond call latency:
- **Python:** [CSnakes](https://github.com/tonybaloney/CSnakes) — source-generates typed C# wrappers from Python type hints, supports .NET 8/9, full pip ecosystem, NumPy↔Span zero-copy
- **JavaScript:** [ClearScript](https://github.com/nickstenning/ClearScript) — Microsoft-maintained V8 embedding, near-native speed, async/Promise↔Task bridging

These could be offered as an opt-in `runtime: "python-embedded"` or `runtime: "v8"` mode in a future phase, with the same manifest and SDK patterns.

### WASM Sandboxing

[Extism](https://extism.org/) enables running extensions compiled to WebAssembly in a secure sandbox with explicit capability grants (filesystem, network). Suitable for untrusted/marketplace extensions but trades off native package access (no pip/npm from within WASM).

### MCP Compatibility

The JSON-RPC protocol defined here is intentionally close to MCP's `tools/call` format. A future adapter could expose any MCP-compatible server as a BotNexus extension, or expose BotNexus tools as MCP tools — enabling interop with the broader MCP ecosystem.

---

## References

- [VS Code Extension Host](https://code.visualstudio.com/api) — process-based extension model, JSON-RPC over stdio
- [MCP Specification](https://modelcontextprotocol.io/specification) — JSON-RPC 2.0 tool calling protocol
- [LSP Specification](https://microsoft.github.io/language-server-protocol/) — language server protocol (JSON-RPC over stdio)
- [Terraform Provider Protocol](https://developer.hashicorp.com/terraform/plugin/framework) — gRPC-based plugin system
- [Extism](https://extism.org/) — WASM-based universal plugin system
- [CSnakes](https://github.com/tonybaloney/CSnakes) — Python↔.NET in-process interop
- [ClearScript](https://github.com/nickstenning/ClearScript) — V8 JavaScript engine embedding for .NET
