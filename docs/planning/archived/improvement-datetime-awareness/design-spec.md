---
id: improvement-datetime-awareness
title: "Agent DateTime Awareness"
type: improvement
priority: medium
status: draft
created: 2026-04-10
updated: 2026-04-10
author: nova
tags: [ux, datetime, timezone, system-prompt]
---

# Design Spec: Agent DateTime Awareness

## Overview

Agents need reliable access to the current date and time in the user's timezone. Currently the system clock is UTC, shell timezone conversion doesn't work in git bash, and the agent has no time injected into its context. This causes embarrassing mistakes like telling the user to go to bed at 9 AM.

## Requirements

### Must Have
1. Agent knows the current date and time in the user's timezone on every turn
2. DST-aware (PDT vs PST handled correctly)
3. No agent effort required (injected automatically)

### Should Have
4. Shell commands (`date`) return time in user's timezone
5. Available to sub-agents and cron jobs too

### Nice to Have
6. A `datetime` tool for querying specific timezones
7. Relative time helpers ("3 hours from now", "next Tuesday")

## Proposed Implementation

### 1. System Prompt Time Injection (Primary Fix)

BotNexus gateway injects a time line into every turn's system prompt:

```
Current time: Friday, April 10, 2026 9:48 AM PDT (America/Los_Angeles)
```

**Implementation:**
- Read `userTimezone` from agent config (or fall back to system timezone)
- Use .NET `TimeZoneInfo` for DST-aware conversion
- Inject as the last line of the system prompt preamble
- Update every turn (not just session start)

**Config:**
```json
{
  "agents": {
    "nova": {
      "userTimezone": "America/Los_Angeles"
    }
  }
}
```

Or inherit from a global default:
```json
{
  "gateway": {
    "defaults": {
      "userTimezone": "America/Los_Angeles"
    }
  }
}
```

### 2. Shell Environment TZ (Secondary Fix)

Set `TZ` environment variable for agent shell sessions:

```json
{
  "agents": {
    "nova": {
      "extensions": {
        "botnexus-exec": {
          "env": {
            "TZ": "America/Los_Angeles"
          }
        }
      }
    }
  }
}
```

This makes `date`, `ls -la`, and other shell commands show local time.

### 3. DateTime Helper Skill (Optional)

The existing `datetime-helper` skill could be enhanced, but the system prompt injection makes this less critical.

## Prior Art

### OpenClaw Implementation
```javascript
function resolveCronStyleNow(cfg, nowMs) {
  const timezone = resolveUserTimezone(cfg?.agents?.defaults?.userTimezone);
  // Uses Intl.DateTimeFormat for DST-aware formatting
  // Returns: { timeLine: "Current time: ...", userTimezone: "America/Los_Angeles" }
}
```

Injected:
- In every agent turn (system prompt)
- In post-compaction context refresh
- In memory flush prompts (with YYYY-MM-DD date stamp)
- In cron job prompts

## Testing Plan

1. Check agent knows correct time at different times of day
2. Verify DST transition handling (March/November boundaries)
3. Verify shell `date` command returns local time
4. Verify cron jobs get correct time context
5. Verify time updates between turns (not stale from session start)

## Quick Win (No Platform Changes)

As an immediate workaround, Nova can add to her session startup:
```bash
python3 -c "from datetime import datetime, timedelta, timezone; print(datetime.now(timezone(timedelta(hours=-7))).strftime('Current time: %A %B %d, %Y %I:%M %p PDT'))"
```

But this is fragile (hardcoded DST offset) and burns a tool call. Platform injection is the right fix.
