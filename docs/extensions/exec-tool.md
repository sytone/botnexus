# Exec Tool

The Exec Tool provides agents with advanced shell command execution including timeouts, background processes, stdin piping, and environment variable merging.

## Overview

| Property | Value |
|----------|-------|
| Extension ID | `botnexus-exec-tool` |
| Tool name | `exec` |
| Source | `BotNexus.Extensions.ExecTool` |

## Capabilities

- Execute commands with configurable timeouts
- Run processes in the background (returns PID immediately)
- Pipe input to stdin
- Set additional environment variables per invocation
- Override working directory
- Kill processes on inactivity (no-output timeout)
- Windows `.cmd`/`.bat` resolution

## Tool Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `command` | string[] | Yes | Command and arguments as an array. First element is the command, rest are args. |
| `timeoutMs` | integer | No | Max execution time in milliseconds. Default: 120000 (2 min). |
| `noOutputTimeoutMs` | integer | No | Kill if no output for this many ms. |
| `input` | string | No | String to pipe to stdin. |
| `background` | boolean | No | If true, start in background and return PID immediately. |
| `env` | object | No | Additional environment variables to set. |
| `workingDir` | string | No | Working directory override. |

## Configuration

The Exec Tool is enabled by default for all agents. No additional configuration is required.

The tool respects the agent's workspace directory as the default working directory. Background processes are tracked in a shared registry and can be managed via the [Process Tool](./process-tool.md).

## Usage Examples

**Simple command:**
```json
{
  "command": ["git", "status"]
}
```

**With timeout and working directory:**
```json
{
  "command": ["npm", "run", "build"],
  "timeoutMs": 300000,
  "workingDir": "/home/user/project"
}
```

**Background process:**
```json
{
  "command": ["npm", "run", "dev"],
  "background": true
}
```

**Piping stdin:**
```json
{
  "command": ["pwsh", "-NoProfile", "-c", "Get-Content -"],
  "input": "Hello from stdin"
}
```

## Behavior Notes

- Output is capped at 100 KB to prevent memory issues with verbose commands.
- Background processes persist across tool calls within the same session and can be managed with the [Process Tool](./process-tool.md).
- On Windows, the tool resolves `.cmd` and `.bat` files automatically when the command is not a full path.
- The default timeout of 2 minutes applies unless overridden. Background processes have a separate 10-minute default.
- When `noOutputTimeoutMs` is set, the process is killed if it produces no stdout/stderr within that window.

## Security

- Commands run with the same permissions as the BotNexus gateway process.
- The `workingDir` parameter is validated against the agent's allowed paths when path policies are configured.
- Environment variables set via `env` are merged with (not replacing) the parent process environment.

## Related

- [Process Tool](./process-tool.md) — Manage background processes started by Exec Tool
- [Shell Execution Guide](/features/shell-execution) — Feature deep-dive on shell execution patterns
