---
id: improvement-memory-lifecycle
title: "Memory Persistence Lifecycle"
type: improvement
priority: high
status: in-progress
created: 2026-04-10
updated: 2026-05-07
author: nova
tags: [memory, compaction, persistence, dreaming]
depends_on: [feature-subagent-spawning]
notes: "Phase 4 (dreaming) depends on sub-agent spawning for consolidation cron jobs"
---

# Design Spec: Memory Persistence Lifecycle

**Type**: Improvement
**Priority**: High (data loss between sessions, core continuity feature)
**Status**: Draft
**Author**: Nova (via Jon)

## Overview

Implement automatic memory persistence triggers so agents write important context to disk before it is lost to compaction or session end. This is a two-layer solution: platform-level automation (like OpenClaw's pre-compaction flush) and agent-level improvements (better prompting and habits).

## Requirements

### Must Have (Platform)
1. **Pre-compaction memory flush**: Before compaction runs, give the agent one turn to write memories to disk
2. **Memory flush prompt**: System-level prompt telling the agent what to persist and where
3. **Post-compaction memory sync**: Re-index memory files after compaction

### Must Have (Agent)
4. **Session startup memory read**: Agent reads recent daily notes + MEMORY.md (already in AGENTS.md)
5. **Periodic memory writes**: Agent writes to daily notes during active work, not just at flush time

### Should Have (Platform)
6. **Session-end flush**: When a session is closed/reset, trigger memory flush first
7. **Memory flush tracking**: Track when last flush happened to avoid duplicates
8. **Configurable flush threshold**: How close to compaction before flush triggers

### Nice to Have (Platform)
9. **Dreaming**: Periodic consolidation of daily notes into MEMORY.md
10. **Short-term promotion**: Track recall frequency, promote frequently-accessed memories
11. **Memory budget visibility**: How much memory content is consuming context

## Proposed Implementation

### Phase 1: Pre-Compaction Memory Flush (Platform)

The core feature from OpenClaw, adapted for BotNexus:

#### Trigger Logic
```
Before compaction runs:
1. Check if memory flush is needed (token count near threshold)
2. Check if flush already ran this compaction cycle
3. If needed: run a special agent turn with the flush prompt
4. Agent writes to memory/YYYY-MM-DD.md
5. Then proceed with normal compaction
```

#### Flush Prompt (injected by platform)
```
Pre-compaction memory flush.
The session is near auto-compaction. Capture important context to disk before
it is summarized away.

Write to: memory/YYYY-MM-DD.md (create memory/ directory if needed)
Rules:
- APPEND only - do not overwrite existing content in the file
- Do NOT modify MEMORY.md, SOUL.md, TOOLS.md, AGENTS.md, or other workspace files
- Use the canonical YYYY-MM-DD.md filename only (no timestamps in filename)
- Capture: decisions made, tasks in progress, important context, open questions
- Skip: routine tool outputs, transient data, things already in memory

If nothing worth persisting, reply with NO_REPLY.
```

#### Config
```json
{
  "gateway": {
    "compaction": {
      "memoryFlush": {
        "enabled": true,
        "softThresholdTokens": 4000,
        "forceFlushTranscriptBytes": 2097152,
        "prompt": null,
        "systemPrompt": null
      }
    }
  }
}
```

#### Safety Guards
- One flush per compaction cycle (tracked in session metadata)
- Cannot modify workspace bootstrap files (only memory/ directory)
- Not triggered during heartbeat sessions
- Timeout: 60 seconds max for flush turn
- If flush fails, compaction proceeds anyway (flush is best-effort)

### Phase 2: Agent-Level Improvements (No Platform Changes)

Things Nova can do immediately:

#### 2a. Periodic Memory Writes During Work
Add to AGENTS.md or make it a habit:
- After completing a significant task: write a note
- After a complex discussion: capture decisions
- Before loading a skill or doing heavy work: checkpoint current state
- During heartbeats: review and persist anything notable

#### 2b. End-of-Conversation Persistence
When detecting conversation is winding down (user says goodbye, long gap, etc.):
- Write session summary to daily notes (`memory/YYYY-MM-DD.md`)
- Do **not** write to `MEMORY.md` — it is read-only during normal turns
- Long-term consolidation into `MEMORY.md` is deferred to Phase 4 (Dreaming)

#### 2c. Memory Section in System Prompt
Enhance the system prompt (via AGENTS.md) with explicit memory guidance:
```markdown
## Memory Persistence
Write to memory/YYYY-MM-DD.md when:
- A decision is made
- A task is completed or its status changes
- Important context is discussed that should survive compaction
- You learn something new about the user, team, or project
- Before loading a large skill or starting context-heavy work

Format: Use headers and timestamps for easy scanning.
```

### Phase 3: Session-End Flush

When the session is being closed (user runs /reset, /new, or explicit close):
1. Platform sends a "session ending" event
2. Agent gets one flush turn (same prompt as pre-compaction)
3. Session is then closed/reset

### Phase 4: Dreaming (Future)

Periodic consolidation via cron job:
1. Read recent daily memory files (last 7-14 days)
2. Identify patterns, frequently referenced items, important decisions
3. Update MEMORY.md with consolidated insights
4. Archive/summarize old daily files

This could be implemented as a cron job that runs a sub-agent with a specific consolidation prompt. Already partially described in AGENTS.md under "Memory Maintenance (During Heartbeats)".

## Implementation Notes

### For the Platform Team
- The pre-compaction flush is modeled exactly on OpenClaw's `memory-flush.ts`
- Key function: `runMemoryFlushIfNeeded()` called before `runCompaction()`
- Needs session metadata fields: `memoryFlushAt`, `memoryFlushCompactionCount`
- The flush runs the agent with `trigger: "memory"` and `silentExpected: true`
- Post-flush, the normal compaction proceeds

### For Nova (Immediate)
- Start writing to `memory/YYYY-MM-DD.md` more proactively
- Add memory persistence reminders to HEARTBEAT.md
- Consider a "memory checkpoint" habit after significant exchanges

## Testing Plan

1. Have a long conversation that triggers compaction -> verify flush writes to memory/
2. Check that flush only runs once per compaction cycle
3. Verify flush cannot modify AGENTS.md, SOUL.md, etc.
4. Test session-end flush on /reset
5. Verify memory search indexes new content after flush
6. Test with flush disabled in config

## Open Questions

1. Should the flush turn use the same model as the main agent, or a cheaper one?
   - OpenClaw uses the same model. Cheaper might be fine since it is just writing notes.
2. Should the flush have access to all tools or just file write tools?
   - OpenClaw gives full tool access. Recommend limiting to read/write/edit only.
3. How to handle the daily note file if it is very large?
   - Append only, but might need a size cap or rotation.
