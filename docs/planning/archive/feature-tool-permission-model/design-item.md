---
id: feature-tool-permission-model
title: "Per-Agent File System Permission Model"
type: feature
priority: high
status: done
created: 2026-04-11
author: Jon Bullen
tags: [security, tools, permissions, agent-config]
---

# Design Item: Per-Agent File System Permission Model

## Problem

Currently, file-system tools (read, write, edit, glob, grep, shell) are locked to the agent's workspace directory (`~/.botnexus/agents/{name}/workspace`). This means:

1. **Agents can't read project files** — Nova can't read `Q:\repos\botnexus\` even though Jon explicitly asks her to review code there
2. **All-or-nothing** — either locked to workspace or requires workarounds (shell `cat` bypasses the check)
3. **No per-agent customization** — every agent gets the same restrictions

## Desired Behavior

A permission model where each agent's file access is configurable:

```json
{
  "agents": {
    "nova": {
      "fileAccess": {
        "allowedPaths": [
          "~/.botnexus/agents/nova/workspace",
          "Q:\\repos\\botnexus"
        ],
        "deniedPaths": [
          "Q:\\repos\\botnexus\\.env",
          "Q:\\repos\\botnexus\\secrets"
        ],
        "mode": "allowlist"
      }
    }
  }
}
```

### Key Requirements

1. **Per-agent path allowlist** — each agent can have different allowed directories
2. **Deny overrides allow** — explicit deny paths take precedence
3. **Read vs Write separation** — agent might read a repo but only write to workspace
4. **Inherited defaults** — default permission set, agents override as needed
5. **Runtime validation** — all file tools (read, write, edit, glob, grep, shell) check permissions
6. **Config-driven** — no code changes to add/remove paths, just config

### Affected Tools

| Tool | File Access | Current Check |
|------|------------|---------------|
| `read` | Read files | Workspace-locked |
| `write` | Create/write files | Workspace-locked |
| `edit` | Modify files | Workspace-locked |
| `glob` | List files by pattern | Workspace-locked |
| `grep` | Search file contents | Workspace-locked |
| `shell/bash` | Execute commands | No path check (bypasses!) |
| `watch_file` | Watch for changes | Needs path check |

### Notes

- Shell/bash currently bypasses file access checks entirely — `cat /etc/passwd` works
- This is a security-critical feature — needs careful design review
- Consider: should the permission model also cover network access, environment variables?
