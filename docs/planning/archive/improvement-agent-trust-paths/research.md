# Research: Configurable Agent Path Trust / Write Permissions

## Problem Statement

The `write` and `edit` tools restrict file operations to the agent's workspace directory. This is a good security default, but it blocks legitimate workflows:

- Nova can't write planning specs to `Q:\repos\botnexus\docs\planning\` using `write`/`edit`
- Workaround is `bash cat >` which bypasses the restriction entirely
- The bash workaround is LESS safe than a configured allowlist would be (bash can write anywhere)

The current security model is self-defeating: the restriction pushes the agent to use an unrestricted tool instead.

## Current State

### Write/Edit Tool
- Path must resolve inside `C:\Users\jobullen\.botnexus\agents\nova\workspace`
- Error: "Path '...' resolves outside working directory"
- No configuration for additional allowed paths

### Bash/Exec Tool
- Can write to any path the OS user has access to
- Has approval controls for elevated commands, but basic file writes go through
- This is what Nova uses to work around the write tool restriction

### The Security Gap
```
Intended: write tool restricted to workspace (safe)
Actual:   agent uses bash to write anywhere (less safe)
Result:   security control provides false sense of safety
```

## Industry Reference

### Claude Code
- File access follows project directory boundaries
- `/add-dir <path>` command adds additional directories for the session
- Permission modes control what level of approval is needed
- AGENTS.md scoping: subdirectory AGENTS.md files scope to that directory

### Aider
- Explicit `/add` and `/read-only` commands for file access
- `.aiderignore` for excluding paths
- No write restrictions beyond what the OS allows

### Windsurf
- `.windsurf/ignore` for exclusions
- Workspace-scoped by default
- No configurable trust paths

## Design Considerations

### Trust Levels
Different agents should have different trust:
- **Nova** (primary assistant, trusted): Access to workspace + repos + planning dirs
- **Sub-agents** (ephemeral, less trusted): Workspace only or specific subdirectories
- **Untrusted/experimental agents**: Strict workspace-only sandbox

### Path Types
- **Workspace** (always allowed): Agent's own workspace directory
- **Trusted paths** (configurable): Additional directories the agent can read/write
- **Read-only paths** (configurable): Can read but not modify
- **Denied paths** (configurable): Explicit blocklist even if parent is trusted
