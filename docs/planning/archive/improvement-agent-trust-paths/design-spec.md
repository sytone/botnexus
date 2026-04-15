---
id: improvement-agent-trust-paths
title: "Configurable Agent File Path Trust"
type: improvement
priority: medium
status: draft
created: 2026-04-10
updated: 2026-04-10
author: nova
tags: [security, permissions, file-access, trust, agent-config]
---

# Design Spec: Configurable Agent File Path Trust

## Overview

Allow per-agent configuration of trusted file paths beyond the workspace directory. This gives trusted agents (like Nova) access to project directories through the proper `read`/`write`/`edit` tools instead of forcing them to use unrestricted `bash` as a workaround.

## Problem

The current write tool restriction to workspace-only is bypassed by using `bash cat > /any/path`. The security control is self-defeating — it pushes agents toward a less controlled tool. A configurable allowlist is both more secure and more functional.

## Requirements

### Must Have
1. Per-agent configurable list of additional trusted paths (read + write)
2. Per-agent configurable list of read-only paths
3. Default remains workspace-only (secure by default)
4. `write`, `edit`, and `read` tools respect the configured paths
5. Paths support both absolute paths and glob patterns

### Should Have
6. Deny list to exclude specific paths even if a parent is trusted
7. Trust inherited by sub-agents is configurable (default: workspace only for sub-agents)
8. Path configuration validation at startup (warn if path doesn't exist)

### Nice to Have
9. Session-scoped temporary path additions (like Claude Code's `/add-dir`)
10. Audit log of out-of-workspace file operations

## Proposed Config

```json
{
  "agents": {
    "nova": {
      "fileAccess": {
        "trustedPaths": [
          "Q:\repos\botnexus\docs\planning",
          "Q:\repos\botnexus\docs\design"
        ],
        "readOnlyPaths": [
          "Q:\repos\botnexus\src"
        ],
        "deniedPaths": [
          "Q:\repos\botnexus\src\secrets"
        ],
        "subAgentInherit": "workspace-only"
      }
    }
  }
}
```

### Path Resolution Rules

1. **Workspace** — always read+write (current behavior)
2. **trustedPaths** — read+write, resolved to absolute paths at startup
3. **readOnlyPaths** — read only via `read` tool, write/edit blocked
4. **deniedPaths** — blocked even if parent directory is in trustedPaths
5. All paths are checked BEFORE the operation (fail fast, clear error message)
6. Paths must be absolute (no relative paths that could escape)
7. Symlink targets are resolved before checking (prevent symlink escapes)

### Path Matching

```
Request: write to Q:\repos\botnexus\docs\planning\feature-x\spec.md

Check order:
  1. Is it in deniedPaths? -> DENY
  2. Is it in workspace? -> ALLOW (read+write)
  3. Is it in trustedPaths? -> ALLOW (read+write)
  4. Is it in readOnlyPaths? -> ALLOW read / DENY write
  5. Default -> DENY with helpful error message
```

### Error Messages

Current (unhelpful):
```
Path 'Q:/repos/...' resolves outside working directory
```

Proposed (actionable):
```
Path 'Q:\repos\botnexus\docs\planning\...' is outside the agent workspace.

To allow access, add to the agent's trustedPaths in config:
  "fileAccess": { "trustedPaths": ["Q:\repos\botnexus\docs\planning"] }
```

## Implementation

### Where to Check

The path validation likely lives in the tool handler for `read`, `write`, `edit`. The change is:

```
Current:  resolve(path) must start with workspaceDir
Proposed: resolve(path) must start with workspaceDir OR any trustedPath OR any readOnlyPath (for reads)
          AND must NOT start with any deniedPath
```

### Config Loading

- Read `fileAccess` from agent config at startup
- Resolve all paths to absolute (handle `~`, env vars)
- Validate paths exist (warn if not, don't fail)
- Cache resolved paths for fast checking

### Bash/Exec Consideration

This improvement does NOT restrict bash — that would require OS-level sandboxing.
But by making the proper tools work for trusted paths, agents have less reason to use bash for file operations. Over time, bash could optionally be restricted to workspace + trustedPaths too.

## Migration

For Nova specifically, the immediate config change would be:

```json
{
  "agents": {
    "nova": {
      "fileAccess": {
        "trustedPaths": [
          "Q:\repos\botnexus\docs\planning"
        ]
      }
    }
  }
}
```

## Testing Plan

1. Write to workspace — allowed (unchanged)
2. Write to trusted path — allowed
3. Write to read-only path — denied
4. Write to denied path inside trusted parent — denied
5. Write to random path — denied with helpful error
6. Read from read-only path — allowed
7. Edit file in trusted path — allowed
8. Sub-agent write to parent's trusted path — denied (default)
9. Symlink from workspace to untrusted path — denied (resolve target first)
10. Glob pattern in trustedPaths — works as expected
