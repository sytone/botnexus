# Shell Execution

**Version:** 1.0  
**Last Updated:** 2026-06-08  
**Status:** Stable

---

## Table of Contents

1. [Overview](#overview)
2. [Shell Preference Modes](#shell-preference-modes)
3. [Custom Shell Command](#custom-shell-command)
4. [Configuration Hierarchy](#configuration-hierarchy)
5. [ArgumentList Execution Model](#argumentlist-execution-model)
6. [Output Handling](#output-handling)
7. [Timeouts and Cancellation](#timeouts-and-cancellation)
8. [Examples](#examples)
9. [Troubleshooting](#troubleshooting)

---

## Overview

The Shell Tool enables agents to execute operating system commands within their working directory. When an agent calls the `shell` (or `bash`) tool, BotNexus spawns a child process with the specified command, captures stdout and stderr, and returns the combined output to the agent.

Key characteristics:

- **Single-command execution** — Each tool invocation starts a fresh shell process. No persistent session state between calls.
- **Working directory scoped** — Commands execute in the agent's configured workspace directory.
- **Platform-adaptive** — Automatically selects an appropriate shell binary based on OS and configuration.
- **Safe argument passing** — Uses .NET `ArgumentList` to eliminate escaping issues (see [ArgumentList Execution Model](#argumentlist-execution-model)).
- **Bounded output** — Output is capped to protect token budgets (see [Output Handling](#output-handling)).

The tool is exposed to the LLM as either `shell` (when preference is `pwsh`) or `bash` (when preference is `auto` or `bash`), with an appropriate description matching the selected shell.

---

## Shell Preference Modes

The `shellPreference` setting controls which shell binary BotNexus uses for command execution. Three modes are available:

| Mode | Tool Name | Behavior |
|------|-----------|----------|
| `auto` | `bash` | Prefers bash (Git Bash on Windows, `/bin/bash` on Unix). Falls back to PowerShell if bash is not found. |
| `pwsh` | `shell` | Always uses PowerShell Core (`pwsh`). Falls back to Windows PowerShell (`powershell.exe`) if `pwsh` is not installed. |
| `bash` | `bash` | Always uses bash. If bash is not found on Windows, falls back to PowerShell with a warning prefix in output. |

### Auto Mode (Default)

On **Windows**, auto mode searches for bash in this order:
1. `C:\Program Files\Git\bin\bash.exe`
2. `C:\Program Files (x86)\Git\bin\bash.exe`
3. `where.exe bash` (PATH lookup)

If no bash is found, falls back to PowerShell with a warning:
```
[warning: bash not found, using PowerShell — install Git for Windows for best compatibility]
```

On **Linux/macOS**, auto mode always uses `/bin/bash`.

### Pwsh Mode

Uses PowerShell Core on all platforms. On Windows, BotNexus checks whether `pwsh` is available via `where.exe pwsh`. If not found, falls back to legacy `powershell.exe`.

Commands are passed with flags: `-NoLogo -NoProfile -NonInteractive -Command <command>`

### Bash Mode

Forces bash usage. On Unix this always succeeds (`/bin/bash`). On Windows, searches the same paths as auto mode. If bash is unavailable, falls back to PowerShell with a warning in the output.

Commands are passed with flags: `-l -c <command>`

---

## Custom Shell Command

The `shellCommand` configuration provides full control over which shell binary and arguments are used, bypassing the preference-based detection entirely.

### Format

```json
"shellCommand": ["<executable>", "<arg1>", "<arg2>", ..., "<argN>"]
```

The array must have **at least 2 elements**:
- `shellCommand[0]` — The shell executable path
- `shellCommand[1..N]` — Base arguments passed before the agent's command

The agent's command text is always appended as the **final argument** after the base args.

### How It Works

When `shellCommand` is configured, BotNexus:
1. Uses `shellCommand[0]` as the `FileName` for `ProcessStartInfo`
2. Adds `shellCommand[1..N]` to `ArgumentList` as base args
3. Appends the agent's command string as the last argument

The entire sequence uses .NET `ArgumentList` — no manual escaping is needed.

### Examples

**PowerShell Core with custom flags:**
```json
"shellCommand": ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]
```

**PowerShell without `-NonInteractive` (allows interactive prompts):**
```json
"shellCommand": ["pwsh", "-NoLogo", "-NoProfile", "-Command"]
```

**Specific bash path:**
```json
"shellCommand": ["/usr/local/bin/bash", "-l", "-c"]
```

**Nushell:**
```json
"shellCommand": ["nu", "-c"]
```

**WSL bash:**
```json
"shellCommand": ["wsl", "bash", "-l", "-c"]
```

### Validation

If `shellCommand` has fewer than 2 elements, it is ignored and BotNexus falls back to preference-based shell detection. This prevents misconfiguration where only an executable is specified without the argument that receives the command.

---

## Configuration Hierarchy

Shell execution settings follow a two-level override chain:

```
Per-agent shellCommand → Gateway shellCommand → Gateway shellPreference → Auto detection
```

### Resolution Order

1. **Per-agent `shellCommand`** — If the agent definition includes `shellCommand`, it is used directly. All other settings are ignored.
2. **Gateway `shellCommand`** — If the gateway config includes `shellCommand`, it is used as the default for all agents that don't specify their own.
3. **Gateway `shellPreference`** — If no `shellCommand` is configured at either level, the `shellPreference` mode (`auto`, `pwsh`, `bash`) drives shell detection.
4. **Auto detection** — If nothing is configured, defaults to `auto` mode.

### Gateway-Level Configuration

Set in `~/.botnexus/config.json` under the `gateway` section:

```json
{
  "gateway": {
    "shellPreference": "pwsh",
    "shellCommand": ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `shellPreference` | string | `"auto"` | Shell mode: `"auto"`, `"pwsh"`, or `"bash"` |
| `shellCommand` | string[] | `null` | Custom shell command array. Overrides `shellPreference` when set. |

### Per-Agent Configuration

Set in the agent definition under `agents.<agentId>`:

```json
{
  "agents": {
    "my-agent": {
      "shellCommand": ["pwsh", "-NoLogo", "-NoProfile", "-Command"]
    }
  }
}
```

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `shellCommand` | string[] | `null` | Per-agent shell command override. Takes priority over gateway settings. |

### Example: Mixed Environment

```json
{
  "gateway": {
    "shellPreference": "pwsh",
    "shellCommand": ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]
  },
  "agents": {
    "linux-agent": {
      "shellCommand": ["/bin/bash", "-l", "-c"]
    },
    "default-agent": {
    }
  }
}
```

In this configuration:
- `linux-agent` uses `/bin/bash -l -c <command>` (per-agent override)
- `default-agent` uses `pwsh -NoLogo -NoProfile -NonInteractive -Command <command>` (inherits gateway `shellCommand`)

---

## ArgumentList Execution Model

BotNexus uses .NET's `ProcessStartInfo.ArgumentList` to pass commands to the shell. This is a deliberate design choice that eliminates an entire class of escaping bugs.

### The Problem (Before PR #1055)

The previous implementation built a single `Arguments` string:

```csharp
// OLD — broken approach
var escaped = command.Replace("\"", "`\"");
startInfo.Arguments = $"-NoLogo -NoProfile -NonInteractive -Command \"{escaped}\"";
```

This broke because of **double-parse escaping**:

1. **OS-level parse** — On Windows, `CreateProcess` parses the `Arguments` string into an argv array using its own quoting rules (C runtime conventions for quote/backslash handling).
2. **Shell-level parse** — PowerShell then parses the `-Command` argument value using *its own* syntax rules.

The backtick escaping only handled double quotes. Characters like `$`, `@`, `{}`, `|`, `;`, and nested quotes all broke unpredictably because:
- The OS-level parser might consume or transform characters before PowerShell sees them
- Different versions of Windows have subtly different `CreateProcess` parsing rules
- The problem is **fundamentally unsolvable** with a single `Arguments` string — you cannot construct one string that both parsers handle correctly for all inputs

### The Solution (ArgumentList)

```csharp
// CURRENT — correct approach
foreach (var arg in invocation.BaseArgs)
{
    startInfo.ArgumentList.Add(arg);
}
startInfo.ArgumentList.Add(invocation.Command);  // Raw command, no escaping
```

When `ArgumentList` is populated, .NET:
1. Handles all OS-level quoting internally using platform-correct rules
2. Guarantees each argument arrives at the target process as a discrete argv entry
3. The shell receives the command string **unmolested** — exactly as the agent wrote it

This means agents can pass any valid shell command without worrying about escaping. Dollar signs, pipes, semicolons, quotes, braces — everything works because there is only **one** parse layer (the shell's own parser).

### Why This Matters for Agents

Agents generate shell commands dynamically. They write PowerShell with `$variables`, pipe chains (`|`), subexpressions (`$()`), and complex quoting. With ArgumentList, these commands execute exactly as written:

```powershell
# All of these work correctly without any escaping
Get-ChildItem | Where-Object { $_.Length -gt 1MB }
$result = @{Name="test"; Value=$env:PATH}
Write-Output "Hello `"World`""
```

---

## Output Handling

Shell output is processed to fit within agent context windows while preserving the most useful information.

### Limits

| Limit | Value | Purpose |
|-------|-------|---------|
| Maximum bytes | 50 KB (51,200 bytes) | Protects token budgets |
| Maximum lines | 2,000 | Prevents runaway line counts |

### Processing Pipeline

1. **Interleaved capture** — stdout and stderr are captured together in arrival order using sequence numbers for correct ordering.
2. **Line limit** — If output exceeds 2,000 lines, only the last 2,000 are retained.
3. **Byte limit** — Working backwards from the tail, lines are accumulated until the 50 KB cap is reached.
4. **ANSI stripping** — ANSI escape codes (colors, cursor movement) are removed from all output lines.
5. **Truncation note** — If any content was truncated, a header is prepended:
   ```
   [output truncated — showing last 847 lines of 12,453]
   ```
6. **Exit code** — Non-zero exit codes append a footer:
   ```
   [command exited with code 1]
   ```

### Design Rationale

The **tail-biased** truncation strategy keeps the most recent output because:
- Error messages typically appear at the end
- Build failures report the cause in trailing lines
- Long-running commands produce progress output at the start that becomes irrelevant

---

## Timeouts and Cancellation

### Default Timeout

The default shell timeout is **600 seconds** (10 minutes). This accommodates legitimate long-running operations like builds, large file operations, and network transfers.

### Per-Call Override

Agents can specify a timeout per invocation:

```json
{
  "command": "dotnet build",
  "timeout": 120
}
```

The `timeout` parameter must be ≥ 1 second.

### Timeout Behavior

When a timeout fires:
1. The process tree is killed (`Process.Kill(entireProcessTree: true)`)
2. BotNexus waits up to 5 seconds for the process to fully terminate
3. Any captured output is returned with a timeout header:
   ```
   Command timed out after 120 seconds.

   <captured output>
   ```
4. The result includes `TimedOut: true` and `ExitCode: -1`

### Cancellation

If the parent operation is cancelled (e.g., session terminated), the same kill-and-return behavior applies, but the message reads:
```
Command cancelled.
```

### Configuration

The default timeout is set when the `ShellTool` is constructed (currently hardcoded at 600s in the gateway). The `CodingAgent` exposes this as `defaultShellTimeoutSeconds` in its config:

```json
{
  "defaultShellTimeoutSeconds": 300
}
```

---

## Examples

### Example 1: Default Auto Configuration (Most Users)

No shell configuration needed — BotNexus auto-detects:

```json
{
  "gateway": {
  }
}
```

- Windows with Git installed → uses Git Bash
- Windows without Git → uses PowerShell with warning
- Linux/macOS → uses `/bin/bash`

### Example 2: Force PowerShell for All Agents

```json
{
  "gateway": {
    "shellPreference": "pwsh"
  }
}
```

All agents use `pwsh -NoLogo -NoProfile -NonInteractive -Command <cmd>`.

### Example 3: Custom Shell Command (Gateway Default)

```json
{
  "gateway": {
    "shellCommand": ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command"]
  }
}
```

Identical to `shellPreference: "pwsh"` but explicit — useful when you want to ensure specific flags are always used.

### Example 4: Per-Agent Override for a Linux Workload

```json
{
  "gateway": {
    "shellPreference": "pwsh"
  },
  "agents": {
    "build-agent": {
      "shellCommand": ["/bin/bash", "-l", "-c"]
    }
  }
}
```

`build-agent` uses bash despite the gateway defaulting to PowerShell.

### Example 5: Nushell for a Specific Agent

```json
{
  "agents": {
    "nu-agent": {
      "shellCommand": ["nu", "-c"]
    }
  }
}
```

### Example 6: WSL Bash on Windows

```json
{
  "agents": {
    "wsl-agent": {
      "shellCommand": ["wsl", "bash", "-l", "-c"]
    }
  }
}
```

---

## Troubleshooting

### "bash not found, using PowerShell" Warning

**Cause:** `shellPreference` is `auto` or `bash`, but no bash executable was found on the system.

**Solutions:**
1. Install [Git for Windows](https://git-scm.com/download/win) (includes Git Bash)
2. Set `shellPreference` to `"pwsh"` if you prefer PowerShell
3. Use `shellCommand` to point to a specific bash location

### Commands with Special Characters Fail

**Cause (historical):** Prior to PR #1055, BotNexus used a single `Arguments` string which caused double-parse issues.

**Current behavior:** With `ArgumentList`, all special characters (`$`, `@`, `|`, `;`, `{}`, etc.) work correctly. If you're experiencing issues:
1. Ensure you're running the latest BotNexus version
2. Check that the command itself is valid for the target shell
3. Review the shell's own error output in the tool result

### Output is Truncated

**Expected behavior.** Output is limited to 50 KB / 2,000 lines to protect agent context windows. The truncation note tells you how much was cut:

```
[output truncated — showing last 500 lines of 8,234]
```

**Workaround:** Redirect output to a file and read relevant sections:
```bash
long-running-command > output.log 2>&1
tail -50 output.log
```

### Command Times Out

**Default timeout:** 600 seconds (10 minutes).

**Solutions:**
1. Pass a longer `timeout` in the tool call: `{"command": "...", "timeout": 1800}`
2. For the CodingAgent, configure `defaultShellTimeoutSeconds` in the agent config
3. Break long operations into smaller steps

### PowerShell "pwsh not found"

**Cause:** PowerShell Core is not installed or not in PATH.

**Solutions:**
1. Install [PowerShell Core](https://github.com/PowerShell/PowerShell/releases)
2. BotNexus automatically falls back to `powershell.exe` (Windows PowerShell) on Windows
3. Use `shellCommand` to specify the full path: `["C:\\Program Files\\PowerShell\\7\\pwsh.exe", "-NoLogo", "-NoProfile", "-Command"]`

### Process Exits with Non-Zero Code but No Error Output

Some commands write errors to stderr which BotNexus captures. Check if:
1. The command produces output only on success (empty output + non-zero exit = silent failure)
2. The command requires interactive input (use `-NonInteractive` flag or provide input via stdin redirect)
3. The command requires environment variables not available in the spawned process

---

## See Also

- [Configuration Reference](/configuration) — Gateway-level shell settings
- [User Guide: Configuration](/user-guide/configuration) — Per-agent shell configuration
- [Agent Execution](/development/agent-execution) — How agents invoke tools
- [Tool Security](/training/tool-security) — Security considerations for shell execution
