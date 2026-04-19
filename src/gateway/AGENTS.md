# Gateway Projects Rules

## Dependency boundary

Projects in `src/gateway/` may depend on:
- `src/agent/` — agent core and provider abstractions
- `src/domain/` — domain primitives and models

**Prohibited dependencies:**
- `src/extensions/` — extensions depend on the gateway, not the other way around. The gateway discovers and loads extensions dynamically via the extension loader.
- `poc/` — proof-of-concept projects are standalone

## Project structure

| Project | Purpose | Allowed deps |
|---------|---------|-------------|
| `BotNexus.Gateway` | Core gateway runtime — agent supervision, isolation, session management, extension loading, dispatch | Agent, Domain, Gateway.Prompts |
| `BotNexus.Gateway.Api` | ASP.NET host — controllers, middleware, Program.cs | Gateway, Domain (no direct SignalR references) |
| `BotNexus.Gateway.Abstractions` | Interfaces for extensions — `IEndpointContributor`, `IApiContributor`, agent/session/channel contracts | Domain only |
| `BotNexus.Gateway.Contracts` | Lightweight shared types — session summaries, config models | Domain only |
| `BotNexus.Gateway.Channels` | Base channel adapter classes (`ChannelAdapterBase`) | Domain, Gateway.Abstractions |
| `BotNexus.Gateway.Sessions` | Session store implementations (File, SQLite, InMemory) | Domain, Gateway.Contracts |
| `BotNexus.Cron` | Cron job scheduling and tool contributions | Agent, Gateway.Contracts, Domain |
| `BotNexus.Memory` | Agent memory store and search tools | Agent, Gateway.Contracts, Domain |
| `BotNexus.Tools` | Core agent tools (workspace, sub-agent, session management) | Agent, Gateway.Contracts, Domain |
| `BotNexus.Gateway.Prompts` | System prompt building and context file management | Domain only |

## Tool architecture

Three independent tools provide command/process execution:

| Tool | Name | Project | Purpose |
|------|------|---------|---------|
| `ShellTool` | `bash` / `shell` | `BotNexus.Tools` (core) | Execute a shell command and return stdout/stderr. Shell selection controlled by `gateway.shellPreference` config. |
| `ExecTool` | `exec` | `BotNexus.Extensions.ExecTool` (extension) | Execute a command with advanced process control: timeouts, background mode, stdin, env vars. Returns JSON with output, exit code, and PID. |
| `ProcessTool` | `process` | `BotNexus.Extensions.ProcessTool` (extension) | General-purpose process management: list, inspect, send input, read output, or kill any process by PID. |

**These are independent tools.** ExecTool and ProcessTool are separate extensions — ProcessTool is not coupled to ExecTool. An agent can use the PID returned by ExecTool (or any other PID) with ProcessTool for lifecycle management.

**ShellTool vs ExecTool:**
- `ShellTool` wraps commands in a shell (`bash -lc` or `pwsh -Command`) — best for quick one-liners.
- `ExecTool` executes commands directly (array format, no shell interpretation) — best for precise process control, background tasks, and stdin piping.

## Key architectural rules

- **Gateway.Api has zero extension knowledge.** No references to SignalR, MCP, or any extension project. Extensions are loaded dynamically from `~/.botnexus/extensions/` by the extension loader.
- **Gateway.Abstractions is the extension contract surface.** Extensions reference Abstractions for interfaces like `IChannelAdapter`, `IEndpointContributor`, `IAgentTool`.
- **Gateway.Contracts is for lightweight shared types.** Types here can be referenced by both gateway internals and extensions without pulling in the full gateway.
