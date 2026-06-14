# Process Tool

The Process Tool enables agents to manage background processes that were started via the [Exec Tool](./exec-tool.md). It provides listing, status checks, output reading, stdin writes, and process termination.

## Overview

| Property | Value |
|----------|-------|
| Extension ID | `botnexus-process-tool` |
| Tool name | `process` |
| Source | `BotNexus.Extensions.ProcessTool` |

## Capabilities

- List all tracked background processes
- Check process status (running, exited, exit code)
- Read process output (stdout + stderr, with tail support)
- Send input to a process's stdin
- Kill a running process

## Tool Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | Action to perform: `status`, `output`, `input`, `kill`, or `list`. |
| `pid` | integer | Conditional | Process ID. Required for `status`, `output`, `input`, and `kill`. |
| `content` | string | Conditional | Content to send to stdin (for `input` action). |
| `tail` | integer | No | Number of lines from end of output (for `output` action). Default: 50. Values above the configured ceiling (`MaxTail`, default 10,000) are clamped; `tail <= 0` still returns the full captured buffer. |
| `timeout` | integer | No | For `status` action: wait up to N ms for the process to produce output. Default: 0 (no wait). |

## Actions

### `list`

Returns all tracked background processes with their PIDs, commands, and running state.

### `status`

Returns whether a process is running or exited, its exit code (if exited), and duration. Optionally waits for output with a timeout.

### `output`

Returns the most recent output from a process. Use `tail` to limit the number of lines returned.

### `input`

Writes content to a process's stdin. Useful for interactive processes expecting input.

### `kill`

Terminates a running process and its entire process tree.

## Configuration

The Process Tool is enabled by default alongside the Exec Tool. No additional configuration is required.

Captured output is bounded two ways:

- The per-process output buffer is a circular buffer capped at 100 KB.
- The `output` action's `tail` parameter is clamped to a configurable ceiling, `MaxTail` (default **10,000** lines), so a single read cannot request an unbounded number of trailing lines. A non-positive `tail` keeps its documented meaning of "return the full buffer".

## Usage Examples

**List all background processes:**
```json
{
  "action": "list"
}
```

**Check process status with wait:**
```json
{
  "action": "status",
  "pid": 12345,
  "timeout": 5000
}
```

**Read last 20 lines of output:**
```json
{
  "action": "output",
  "pid": 12345,
  "tail": 20
}
```

**Send input to a process:**
```json
{
  "action": "input",
  "pid": 12345,
  "content": "yes\n"
}
```

**Kill a process:**
```json
{
  "action": "kill",
  "pid": 12345
}
```

## Behavior Notes

- The Process Tool shares its process registry with the Exec Tool — it only manages processes started via `exec` with `background: true`.
- Process output is buffered in memory. Very long-running processes may accumulate significant output.
- The `kill` action terminates the entire process tree, not just the root process.
- Process state persists within a session but does not survive gateway restarts.

## Related

- [Exec Tool](./exec-tool.md) — Start background processes
- [Shell Execution Guide](/features/shell-execution) — Feature deep-dive on shell execution patterns
