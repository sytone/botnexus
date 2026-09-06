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
- Windows `.cmd`/`.bat`/`.ps1` shim resolution

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

- Output is capped at 100 KB to prevent memory issues with verbose commands. When the cap is hit,
  the result is prefixed with a banner disclosing the exact loss, for example:

  ```text
  [output truncated: retained 102204 bytes (head) of 150300 bytes produced, discarded 48096 bytes (tail) at the 100KB cap]
  ```

  The **head** of the stream is what survives; everything after the cap is dropped. Use the
  discarded figure to decide whether to re-run with a narrower command or redirect output to a file.
  Output below the cap is returned verbatim with no banner.
- Background processes persist across tool calls for the launching agent within the gateway process.
  The shared Core registry owns the child handle, stdin, and stdout/stderr drains after `exec` returns.
  The [Process Tool](./process-tool.md) can manage only that agent's registered children; another agent
  cannot access them even when it shares the same workspace. A `workingDir` override never changes ownership.
- Background output retains the **tail**, bounded to 100 KB, and discloses discarded head bytes.
  Both streams are drained in bounded chunks, including long lines with no newline. Foreground output
  retains the **head** as described above. `process status` with `timeoutMs` waits for exit and final
  output drain, not just the first output byte; cancelling that wait does not kill the child.
- Without `input`, background stdin stays open for `process input`. With `input`, the supplied text
  is written while output drains and stdin is closed to signal EOF.
- There is no automatic completion wake in this implementation. Use `process status`/`output` to inspect
  work and `process kill` for explicit cleanup. Kill requests target the tree before the root disappears;
  unconfirmed kills remain tracked and re-killable. OS root exit does not prove every descendant exited.
  Completed entries are retained up to a shared cap of 100, oldest first; live and unconfirmed-kill entries
  are never evicted. Registrations are in-memory and are not reattached after a gateway restart.
- `No tracked process` means absent or not owned by this agent, not proof that a command never ran or
  that its OS process has died. Never launch a duplicate side effect based only on that message.
- On Windows, the tool resolves `.exe`, `.cmd`, `.bat` and `.ps1` files from `PATH` automatically when the
  command is not a full path. A `.cmd`/`.bat` shim is launched through `cmd.exe /d /s /c`; a `.ps1` shim is
  launched through `pwsh -NoProfile -File` (falling back to `powershell.exe` when `pwsh` is not on `PATH`),
  with the remaining arguments passed through unmodified. This is why `exec ["qmd", "--version"]` works even
  though `qmd` is an npm-installed PowerShell script rather than an executable.
- If a resolved command still cannot be started, the failure names the resolved path that was attempted and the
  host it was launched through, rather than the bare OS text `The system cannot find the path specified.`
- Foreground execution uses `timeoutMs` (default 2 minutes) and optional `noOutputTimeoutMs`.
  These execution timers do **not** apply to background children. The tool's 10-minute executor timeout
  bounds the launching call, not the background child's lifetime. Startup cancellation kills the started
  child before returning; after successful ownership transfer, cancellation of the launching call does
  not terminate the child. Use a child-native timeout or `process kill` to bound background work.

## Failure Dispositions - Is It Safe To Retry?

Every non-successful `exec` result carries a **disposition** stating how much the platform actually
knows about whether the command's side effect happened. Before this existed a flat error string made
a no-output-timeout kill indistinguishable from a command that never started - and the natural
recovery from a flat error is to rerun it, which for a non-idempotent command executes the side
effect a second time.

| Disposition | Meaning | Retry-safe? |
|---|---|---|
| Completed | The command ran to completion; its exit status is authoritative. | n/a - the result is final |
| `[not-dispatched]` | The command **provably never started**, so no side effect can have occurred. | **Yes**, once the underlying cause is resolved |
| `[outcome-unknown]` | The command was dispatched but no authoritative result was obtained (killed on a timeout, a no-output timeout, or cancellation). It **may** have completed its side effect. | **No** - verify externally first |

The two markers prefix guidance appended to the tool result, so the calling agent reads the
retry-safety instruction directly rather than inferring it from the error text. Treat
`[outcome-unknown]` as "I do not know", never as "it failed": for a non-idempotent command (a push, a
deployment, a payment, a `git commit`) check the target system's state before doing anything else.

## Security

- Commands run with the same permissions as the BotNexus gateway process.
- The `workingDir` parameter is validated against the agent's allowed paths when path policies are configured.
- Environment variables set via `env` are merged with (not replacing) the parent process environment.

## Related

- [Process Tool](./process-tool.md) — Manage background processes started by Exec Tool
- [Shell Execution Guide](/features/shell-execution) — Feature deep-dive on shell execution patterns
