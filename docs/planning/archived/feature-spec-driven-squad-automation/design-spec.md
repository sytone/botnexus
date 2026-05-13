---
id: feature-spec-driven-squad-automation
title: "Spec-Driven Squad Automation"
type: feature
priority: medium
status: draft
created: 2025-07-23
tags: [squad, automation, planning, workflow]
---

# Spec-Driven Squad Automation

## Problem

Today, Squad picks up work only when manually prompted. Design specs sit in `docs/planning/` with status fields but nothing acts on state transitions. The human has to copy-paste spec paths into Squad prompts and babysit execution.

## Proposal

Use spec `status` as a state machine that drives Squad automation:

```
draft  -->  in-progress  -->  delivered  -->  done  -->  archived
  |            |                  |              |           |
  Human        Squad picks up    Squad delivers Human       Human/Nova
  writes       automatically     PR + updates   reviews     archives
               & starts work     status         & approves
```

### State Transitions

| From | To | Triggered By | What Happens |
|------|----|-------------|--------------|
| (new) | `draft` | Human/Nova | Spec created. Squad ignores it. Safe to iterate. |
| `draft` | `in-progress` | Human | **Gate**: Human signals spec is ready for implementation. Squad picks it up. |
| `in-progress` | `delivered` | Squad | Squad has finished work, PR created, spec updated with delivery notes. |
| `delivered` | `done` | Human | **Gate**: Human reviews PR + spec, approves. |
| `done` | archived/ | Human/Nova | Folder moved to `archived/`. Bookkeeping. |

### Key Constraints

- **Squad NEVER touches `draft` specs** -- this is the safe drafting zone
- **Squad NEVER transitions to `done`** -- human review is mandatory
- **Only `in-progress` triggers work** -- explicit human opt-in
- **One spec at a time** (initially) -- avoid parallel conflicts

## Detection Mechanism

### Option A: Cron-Based Polling (Simple)

A cron job runs every N minutes, scans `docs/planning/*/design-spec.md` for `status: in-progress`, and dispatches to Squad if not already working.

```
Cron (every 15 min)
  -> Scan planning folder
  -> Find specs with status: in-progress
  -> Check if Squad already working on it (lock file or state)
  -> If not, spawn Squad with spec path
```

### Option B: File Watcher (Responsive)

Watch `docs/planning/` for file changes. On save, parse frontmatter. If status changed to `in-progress`, dispatch.

### Option C: Git Hook (PR-Friendly)

Post-commit hook detects status changes in planning specs and triggers Squad. Works well if specs are edited via PR.

**Recommendation**: Start with Option A (cron). Simple, debuggable, no infrastructure. Upgrade later if needed.

## Squad Dispatch

When a spec is picked up:

1. Read the full `design-spec.md` + any `research.md` in the folder
2. Spawn Squad agent (or invoke via `agent_converse`) with the spec as context
3. Squad creates a feature branch: `squad/<spec-id>`
4. Squad implements per the spec
5. Squad updates spec status to `delivered` and adds delivery notes section
6. Squad creates PR (or leaves branch ready for PR)
7. Notification sent to human (via Nova's channel)

## Safety Guardrails

- **No auto-merge** -- PRs always require human review
- **Spec lock** -- while Squad is working, a `.lock` file prevents re-dispatch
- **Timeout** -- if Squad hasn't delivered in N hours, notify human and release lock
- **Kill switch** -- set spec back to `draft` to abort (Squad checks periodically)
- **Branch isolation** -- all work on feature branches, never main
- **Dry run mode** -- initial rollout: Squad plans but doesn't execute, human approves plan first

## Notification Flow

```
Spec -> in-progress: "Squad picked up [spec-id], starting work"
Spec -> delivered:    "Squad finished [spec-id], PR ready for review: [link]"
Spec -> done:         "Archived [spec-id]. Nice."
Timeout:              "Squad stalled on [spec-id] after Xh -- intervene?"
```

## Dependencies

- `feature-extension-contributed-commands` (archived) -- not blocking but `/squad status` command would be nice
- Planning management skill -- defines the spec template and lifecycle
- Squad agent accessible via `agent_converse` or sub-agent spawn

## Open Questions

1. **Parallelism**: Start with 1 spec at a time. Multi-spec later?
2. **Priority ordering**: If multiple specs are `in-progress`, which first? Use `priority` field?
3. **Spec size**: Should we limit spec complexity for auto-dispatch? Small/medium only?
4. **Review SLA**: Notify again if `delivered` sits unreviewed for >24h?
5. **Squad model**: Which model/thinking level for auto-dispatched work? Configurable per spec?

## Phases

### Phase 1: Manual Trigger, Auto-Execute
- Nova (or human) sets spec to `in-progress`
- Cron detects and dispatches to Squad
- Squad delivers, notifies

### Phase 2: Smart Scheduling
- Priority ordering
- Parallelism (2-3 specs)
- Model/budget selection per spec complexity

### Phase 3: Feedback Loop
- Squad learns from review feedback (rejected PRs, revision requests)
- Auto-retry with corrections
- Metrics: delivery rate, review pass rate, avg time-to-done
