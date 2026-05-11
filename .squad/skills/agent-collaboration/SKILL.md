---
name: agent-collaboration
description: Standard collaboration protocol for all squad agents
domain: team-workflow
confidence: high
source: extracted from 11 agent charters during reskill optimization
---

## Context

All squad agents follow the same collaboration protocol. This was previously duplicated in every charter (~650 bytes each). Now injected via spawn prompt; this skill documents the canonical protocol for reference.

## Patterns

### Worktree Awareness
- Run `git rev-parse --show-toplevel` to find the repo root, or use the `TEAM ROOT` from the spawn prompt
- All `.squad/` paths resolve relative to the repo root — never assume CWD is root

### Decision Flow
- Before starting: read `.squad/decisions.md` for team decisions that affect you
- After deciding something others need: write to `.squad/decisions/inbox/{agent}-{brief-slug}.md`
- Scribe merges inbox → `decisions.md` (agents don't edit decisions.md directly)
- If you need another team member's input, say so — the coordinator brings them in

### Commit Protocol
1. `git add` specific files (never blanket `git add .`)
2. `git commit` with a clear message describing what and why
3. Include `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` as a trailer

### Review Rejection Protocol
- On rejection, a different agent revises (not the original author)
- The coordinator enforces this rotation

## Anti-Patterns

- Don't blanket `git add .` — be explicit about staged files
- Don't edit `decisions.md` directly — use the inbox
- Don't assume CWD is repo root in a worktree environment
