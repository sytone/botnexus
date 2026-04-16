# Research: Agent DateTime Awareness

## Problem Statement

Nova cannot reliably determine the current time in the user's timezone. This leads to:
- Telling the user to "go rest" at 9 AM on a Friday
- Incorrect time references in conversation
- Unable to make time-appropriate decisions (greetings, scheduling, urgency assessment)

## Current State

### System Clock
- Dev box (CPC-jobul-EYUC8) clock is UTC
- Windows reports Pacific Standard Time but bash tools don't convert properly
- `TZ="America/Los_Angeles" date` in git bash outputs GMT instead of PDT
- No TZ environment variable set in the agent's shell

### What Nova Knows
- USER.md says: `Timezone: Pacific (America/Los_Angeles)`
- But there's no reliable way to get current time in that timezone from shell
- Python works: `datetime.now(timezone(timedelta(hours=-7)))` but requires knowing DST offset
- The runtime header doesn't include current time

### What OpenClaw Does
- `resolveCronStyleNow()` function generates a time line with user timezone
- Injects `Current time: Friday, April 10, 2026 9:48 AM PDT` into prompts
- Post-compaction context refresh includes the time line
- Config has `agents.defaults.userTimezone` field
- Uses `Intl.DateTimeFormat` for proper timezone handling (DST-aware)

### What Claude Code Does
- Injects current time into system prompt
- Uses system locale for timezone detection

## Options

### Option A: Inject Time into System Prompt (Platform)
BotNexus injects a `Current time:` line into every turn's system prompt.
- Simplest, most reliable
- Always accurate, DST-aware
- No agent effort needed
- OpenClaw already does this

### Option B: Provide a `datetime` Tool (Platform)
A tool the agent can call to get current time in any timezone.
- More flexible (can check multiple timezones)
- Costs a tool call each time
- Agent has to remember to call it

### Option C: Set TZ Environment Variable (Config)
Set `TZ=America/Los_Angeles` in the agent's shell environment.
- Fixes `date` command output
- Doesn't help with system prompt awareness
- Doesn't fix DST handling in all tools

### Recommendation: Option A + C
- Inject time into system prompt (always know the time)
- Also set TZ env var (so shell commands return correct local time)
